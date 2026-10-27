using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sitim.Core.Entities;
using Sitim.Core.Options;
using Sitim.Core.Services;
using Sitim.Infrastructure.Data;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sitim.Infrastructure.Services;

/// <summary>
/// FL session lifecycle manager.
/// Handles orchestration between SITIM and external FL control plane.
/// </summary>
public sealed class FLOrchestrationService : IFLOrchestrationService
{
    private readonly AppDbContext _db;
    private readonly ILogger<FLOrchestrationService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FederatedLearningOptions _options;
    private readonly IModelStorageService _modelStorage;

    /// <summary>
    /// The control plane serialises with Python's json module, which emits the bare tokens
    /// NaN / Infinity for non-finite floats. Those are not valid JSON (RFC 8259 allows only
    /// finite numbers) and System.Text.Json rejects them by default — which cost an entire
    /// training run: one client reported a NaN training loss, and from that round on the
    /// whole session payload failed to deserialise, so no further round was ever ingested.
    ///
    /// The control plane now sends null instead, but parsing stays tolerant so a single bad
    /// metric can never again take down the ingestion of everything around it.
    ///
    /// NOTE: AllowNamedFloatingPointLiterals alone is NOT enough. It only accepts the QUOTED
    /// tokens ("NaN"), whereas Python writes them bare — which is a number-literal syntax
    /// error that no NumberHandling setting forgives. Hence the textual pass in
    /// <see cref="ReadControlPlaneJsonAsync{T}"/>; the flag is kept for the quoted spelling.
    /// </summary>
    private static readonly JsonSerializerOptions ControlPlaneJsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>
    /// Matches bare NaN / Infinity / -Infinity where a JSON *value* is expected, i.e. right
    /// after ':' or '[' or ',' and right before ',' or '}' or ']'. Anchoring to those
    /// positions keeps the rewrite away from the same letters appearing inside strings.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex BareNonFiniteNumber = new(
        @"(?<=[:\[,]\s*)(?:NaN|-?Infinity)(?=\s*[,\]\}])",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Reads a control-plane response, neutralising the non-standard numeric literals Python
    /// emits before handing the text to System.Text.Json. A missing metric becomes null
    /// rather than failing the whole payload.
    /// </summary>
    private static async Task<T?> ReadControlPlaneJsonAsync<T>(HttpContent content, CancellationToken cancellationToken)
    {
        var raw = await content.ReadAsStringAsync(cancellationToken);
        var sanitised = BareNonFiniteNumber.Replace(raw, "null");
        return JsonSerializer.Deserialize<T>(sanitised, ControlPlaneJsonOptions);
    }

    /// <summary>
    /// decimal cannot represent NaN or Infinity — Convert.ToDecimal throws OverflowException
    /// on both — so a non-finite metric is stored as "no value" rather than failing the write.
    /// </summary>
    private static decimal? ToDecimalOrNull(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? Convert.ToDecimal(value.Value) : null;

    public FLOrchestrationService(
        AppDbContext db,
        ILogger<FLOrchestrationService> logger,
        IHttpClientFactory httpClientFactory,
        IModelStorageService modelStorage,
        Microsoft.Extensions.Options.IOptions<FederatedLearningOptions> options)
    {
        _db = db;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _modelStorage = modelStorage;
        _options = options.Value;
    }

    public async Task<FLSession> StartSessionAsync(
        string modelKey,
        int totalRounds,
        IReadOnlyCollection<Guid> institutionIds,
        string? architecture,
        Guid createdByUserId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelKey))
            throw new InvalidOperationException("Model key is required.");
        if (totalRounds <= 0)
            throw new InvalidOperationException("Total rounds must be greater than zero.");
        if (institutionIds == null || institutionIds.Count == 0)
            throw new InvalidOperationException("At least one institution is required.");

        var uniqueInstitutionIds = institutionIds.Distinct().ToList();
        var institutions = await _db.Institutions
            .Where(i => uniqueInstitutionIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Name, i.IsActive })
            .ToListAsync(cancellationToken);

        if (institutions.Count != uniqueInstitutionIds.Count)
            throw new InvalidOperationException("One or more institutions do not exist.");
        if (institutions.Any(i => !i.IsActive))
            throw new InvalidOperationException("All participating institutions must be active.");

        var activeSession = await _db.FLSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.Status == FLSessionStatus.Pending || s.Status == FLSessionStatus.Running,
                cancellationToken);
        if (activeSession is not null)
            throw new InvalidOperationException(
                $"Another FL session is already active ({activeSession.Id}). Stop or complete it before starting a new one.");

        var connectedClients = await GetAvailableClientsAsync(cancellationToken);
        var connectedInstitutionIds = connectedClients
            .Where(c => c.IsOnline)
            .Select(c => c.InstitutionId)
            .ToHashSet();
        var missingInstitutionIds = uniqueInstitutionIds
            .Where(id => !connectedInstitutionIds.Contains(id))
            .ToList();

        if (missingInstitutionIds.Count > 0)
        {
            var missingInstitutionLabels = institutions
                .Where(i => missingInstitutionIds.Contains(i.Id))
                .Select(i => $"{i.Name} ({i.Id})")
                .OrderBy(v => v)
                .ToList();

            throw new InvalidOperationException(
                "Missing online FL clients for selected institutions: " +
                string.Join(", ", missingInstitutionLabels) +
                ". Configure each FL client with FL_INSTITUTION_ID=<InstitutionId GUID> and verify registration to control plane.");
        }

        var session = new FLSession
        {
            Id = Guid.NewGuid(),
            ModelKey = modelKey.Trim(),
            Architecture = string.IsNullOrWhiteSpace(architecture) ? null : architecture.Trim().ToLowerInvariant(),
            Status = FLSessionStatus.Pending,
            TotalRounds = totalRounds,
            CurrentRound = 0,
            CreatedByUserId = createdByUserId,
            CreatedAtUtc = DateTime.UtcNow
        };

        session.Participants = uniqueInstitutionIds
            .Select(institutionId => new FLParticipant
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                InstitutionId = institutionId,
                Status = FLParticipantStatus.Invited
            })
            .ToList();

        _db.FLSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var request = new StartExternalSessionRequest(
                session.Id.ToString("D"),
                session.ModelKey,
                session.Architecture,
                session.TotalRounds,
                uniqueInstitutionIds.Select(id => id.ToString("D")).ToList(),
                _options.MinAvailableClients,
                _options.MinFitClients,
                _options.MinEvaluateClients);

            var client = CreateControlPlaneClient();
            var response = await client.PostAsJsonAsync("sessions", request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"FL control plane rejected session start: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
            }

            var externalState = await ReadControlPlaneJsonAsync<ExternalSessionStatusResponse>(response.Content, cancellationToken)
                ?? throw new InvalidOperationException("FL control plane returned an empty start-session response.");

            session.ExternalSessionId = string.IsNullOrWhiteSpace(externalState.SessionId)
                ? session.Id.ToString("D")
                : externalState.SessionId;
            ApplyExternalState(session, externalState);
            ApplyRoundState(session, externalState.Rounds ?? []);
            ApplyParticipantState(session, externalState.Participants ?? []);
            await EnsureModelRegistryEntryForCompletedSessionAsync(session, externalState, cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            session.Status = FLSessionStatus.Failed;
            session.LastError = "Failed to start session on FL control plane.";
            session.FinishedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            throw;
        }

        _logger.LogInformation(
            "Created FL session {SessionId} model {ModelKey} rounds {Rounds} participants {Participants}",
            session.Id,
            session.ModelKey,
            session.TotalRounds,
            session.Participants.Count);

        return session;
    }

    public async Task<IReadOnlyList<FLSession>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        await RefreshRunningSessionsAsync(cancellationToken);

        return await _db.FLSessions
            .AsNoTracking()
            .Include(s => s.Participants)
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> RefreshRunningSessionsAsync(CancellationToken cancellationToken)
    {
        var refreshTargets = await _db.FLSessions
            .AsNoTracking()
            .Where(s =>
                (s.Status == FLSessionStatus.Pending || s.Status == FLSessionStatus.Running) &&
                !string.IsNullOrWhiteSpace(s.ExternalSessionId))
            .Select(s => new { s.Id, s.ExternalSessionId })
            .ToListAsync(cancellationToken);

        var refreshedCount = 0;
        foreach (var target in refreshTargets)
        {
            try
            {
                await RefreshSessionFromControlPlaneAsync(target.Id, target.ExternalSessionId!, cancellationToken);
                refreshedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed refreshing FL session {SessionId} (external {ExternalSessionId})",
                    target.Id,
                    target.ExternalSessionId);
            }
        }

        return refreshedCount;
    }

    public async Task<FLPublishedModel?> GetPublishedModelForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var sessionIdentity = await _db.FLSessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new
            {
                Task = s.ModelKey,
                Version = $"fl_{s.Id:N}"
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (sessionIdentity is null)
            return null;

        var model = await _db.AIModels
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.Task == sessionIdentity.Task && m.Version == sessionIdentity.Version,
                cancellationToken);
        if (model is null)
            return null;

        return new FLPublishedModel(
            model.Id,
            model.Name,
            model.Task,
            model.Version,
            model.StorageFileName,
            model.IsActive);
    }

    public async Task<FLPublishedModel> ActivateSessionModelAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await _db.FLSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            ?? throw new InvalidOperationException($"FL session not found: {sessionId}");

        if (session.Status != FLSessionStatus.Completed)
            throw new InvalidOperationException($"FL session {sessionId} is not completed.");
        if (string.IsNullOrWhiteSpace(session.OutputModelPath))
            throw new InvalidOperationException($"FL session {sessionId} has no output model path.");

        var sessionTask = session.ModelKey.Trim();
        var sessionVersion = $"fl_{session.Id:N}";

        var model = await _db.AIModels
            .FirstOrDefaultAsync(m => m.Task == sessionTask && m.Version == sessionVersion, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Published model for session {sessionId} not found in registry.");

        var modelExistsInStorage = await _modelStorage.ModelExistsAsync(model.StorageFileName, cancellationToken);
        if (!modelExistsInStorage)
            throw new InvalidOperationException(
                $"Model artifact '{model.StorageFileName}' not found in storage.");

        if (string.IsNullOrWhiteSpace(model.OnnxInputSpec) || string.IsNullOrWhiteSpace(model.OnnxOutputSpec))
            throw new InvalidOperationException(
                $"Model metadata for session {sessionId} is incomplete (ONNX specs missing).");

        var modelsSameTask = await _db.AIModels
            .Where(m => m.Task == model.Task && m.IsActive)
            .ToListAsync(cancellationToken);
        foreach (var activeModel in modelsSameTask)
            activeModel.IsActive = false;

        model.IsActive = true;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Activated FL model {ModelId} for task {Task} from session {SessionId}.",
            model.Id,
            model.Task,
            sessionId);

        return new FLPublishedModel(
            model.Id,
            model.Name,
            model.Task,
            model.Version,
            model.StorageFileName,
            model.IsActive);
    }

    public async Task<FLSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var refreshTarget = await _db.FLSessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.ExternalSessionId, s.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (refreshTarget is not null
            && !string.IsNullOrWhiteSpace(refreshTarget.ExternalSessionId)
            && (refreshTarget.Status == FLSessionStatus.Pending || refreshTarget.Status == FLSessionStatus.Running))
        {
            try
            {
                await RefreshSessionFromControlPlaneAsync(sessionId, refreshTarget.ExternalSessionId!, cancellationToken);
            }
            // The refresh is opportunistic: the rows this read returns are already in the
            // database, kept current by FLSessionMonitorHangfireJob. A read must therefore
            // never fail because the refresh did — which is exactly what happened while a
            // session was running. The details page polls every 5 s, and under training load
            // the single-threaded control plane exceeds HttpTimeoutSeconds (TaskCanceled) or
            // loses a duplicate-key race on its final retry attempt (DbUpdateException, whose
            // `when (attempt < maxAttempts)` guard no longer applies). Neither type was caught
            // here, so the request 500'd and SitimApiClient turned that into a null — rendered
            // as "The FL session was not found" for a session that plainly exists.
            //
            // ListSessionsAsync already swallows every refresh failure the same way; this
            // brings the single-session read in line with it.
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed refreshing FL session {SessionId} on read — returning cached DB state.",
                    sessionId);
                _db.ChangeTracker.Clear();
            }
        }

        return await _db.FLSessions
            .AsNoTracking()
            .Include(s => s.Rounds.OrderBy(r => r.RoundNumber))
            .Include(s => s.ModelUpdates)
            .Include(s => s.Participants)
            .ThenInclude(p => p.Institution)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
    }

    public async Task<IReadOnlyList<FLConnectedClient>> GetAvailableClientsAsync(CancellationToken cancellationToken)
    {
        var externalClients = await FetchConnectedClientsAsync(cancellationToken);
        return externalClients
            .Where(c => Guid.TryParse(c.InstitutionId, out _))
            .Select(c => new FLConnectedClient(
                Guid.Parse(c.InstitutionId),
                c.ClientId,
                c.Status,
                c.LastHeartbeatUtc,
                c.IsOnline))
            .OrderBy(c => c.InstitutionId)
            .ToList();
    }

    public async Task<bool> StopSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await _db.FLSessions
            .Include(s => s.Participants)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session == null)
            return false;

        if (session.Status is FLSessionStatus.Completed or FLSessionStatus.Failed or FLSessionStatus.Stopped)
            return true;

        if (!string.IsNullOrWhiteSpace(session.ExternalSessionId))
        {
            var client = CreateControlPlaneClient();
            var response = await client.PostAsync($"sessions/{session.ExternalSessionId}/stop", null, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"FL control plane stop failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
            }
        }

        session.Status = FLSessionStatus.Stopped;
        session.FinishedAtUtc = DateTime.UtcNow;
        session.LastError = "Stopped by user.";
        foreach (var participant in session.Participants)
        {
            if (participant.Status is FLParticipantStatus.Invited or FLParticipantStatus.Accepted or FLParticipantStatus.Training)
                participant.Status = FLParticipantStatus.Stopped;
            participant.LastHeartbeatUtc ??= DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Stopped FL session {SessionId}", sessionId);
        return true;
    }

    private HttpClient CreateControlPlaneClient() =>
        _httpClientFactory.CreateClient(FederatedLearningOptions.ControlPlaneHttpClientName);

    private async Task<List<ExternalClientStatusDto>> FetchConnectedClientsAsync(CancellationToken cancellationToken)
    {
        var client = CreateControlPlaneClient();
        var response = await client.GetAsync("clients", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"FL control plane client-list fetch failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        var payload = await ReadControlPlaneJsonAsync<ExternalClientsResponse>(response.Content, cancellationToken)
            ?? throw new InvalidOperationException("FL control plane returned an empty clients payload.");

        return payload.Clients ?? [];
    }

    private async Task RefreshSessionFromControlPlaneAsync(Guid sessionId, string externalSessionId, CancellationToken cancellationToken)
    {
        var client = CreateControlPlaneClient();
        var response = await client.GetAsync($"sessions/{externalSessionId}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await HandleMissingExternalSessionAsync(sessionId, externalSessionId, cancellationToken);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"FL control plane status fetch failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        var externalState = await ReadControlPlaneJsonAsync<ExternalSessionStatusResponse>(response.Content, cancellationToken)
            ?? throw new InvalidOperationException("FL control plane returned an empty status payload.");

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var session = await _db.FLSessions
                    .Include(s => s.Rounds)
                    .Include(s => s.Participants)
                    .Include(s => s.ModelUpdates)
                    .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

                if (session is null)
                    return;

                ApplyExternalState(session, externalState);
                ApplyRoundState(session, externalState.Rounds ?? []);
                ApplyParticipantState(session, externalState.Participants ?? []);
                await ApplyTimeoutPolicyAsync(session, externalSessionId, cancellationToken);
                await EnsureModelRegistryEntryForCompletedSessionAsync(session, externalState, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Concurrency conflict while refreshing FL session {SessionId} (attempt {Attempt}/{MaxAttempts})",
                    sessionId,
                    attempt,
                    maxAttempts);
                _db.ChangeTracker.Clear();
                await Task.Delay(100 * attempt, cancellationToken);
            }
            catch (DbUpdateException ex) when (attempt < maxAttempts && IsUniqueViolation(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Duplicate-key race while refreshing FL session {SessionId} (attempt {Attempt}/{MaxAttempts}); retrying.",
                    sessionId,
                    attempt,
                    maxAttempts);
                _db.ChangeTracker.Clear();
                await Task.Delay(100 * attempt, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Failed to refresh FL session {sessionId} after {maxAttempts} retries due to concurrent updates.");
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private async Task HandleMissingExternalSessionAsync(
        Guid sessionId,
        string externalSessionId,
        CancellationToken cancellationToken)
    {
        var session = await _db.FLSessions
            .Include(s => s.Participants)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
            return;

        if (session.Status is not (FLSessionStatus.Pending or FLSessionStatus.Running))
            return;

        var graceMinutes = Math.Max(1, _options.MissingExternalSessionGraceMinutes);
        var anchorTime = session.StartedAtUtc ?? session.CreatedAtUtc;
        var graceDeadlineUtc = anchorTime.AddMinutes(graceMinutes);

        if (DateTime.UtcNow < graceDeadlineUtc)
        {
            _logger.LogWarning(
                "External FL session {ExternalSessionId} missing while local session {SessionId} is active; waiting grace window ({GraceMinutes} min).",
                externalSessionId,
                sessionId,
                graceMinutes);
            return;
        }

        session.Status = FLSessionStatus.Failed;
        session.FinishedAtUtc = DateTime.UtcNow;
        session.LastError = $"External FL session '{externalSessionId}' not found after grace window ({graceMinutes} min).";
        foreach (var participant in session.Participants)
        {
            if (participant.Status is FLParticipantStatus.Invited or FLParticipantStatus.Accepted or FLParticipantStatus.Training)
                participant.Status = FLParticipantStatus.Failed;
            participant.LastHeartbeatUtc ??= DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogError(
            "Marked local FL session {SessionId} as failed because external session {ExternalSessionId} is missing.",
            sessionId,
            externalSessionId);
    }

    private static void ApplyExternalState(FLSession session, ExternalSessionStatusResponse externalState)
    {
        session.Status = MapSessionStatus(externalState.Status);
        // The control plane is authoritative: it records what actually trained, which also
        // backfills sessions started before the UI could choose an architecture.
        session.Architecture ??= externalState.Architecture;
        session.TotalRounds = externalState.TotalRounds > 0 ? externalState.TotalRounds : session.TotalRounds;
        session.CurrentRound = Math.Max(0, externalState.CurrentRound);
        session.StartedAtUtc = externalState.StartedAtUtc ?? session.StartedAtUtc;
        session.FinishedAtUtc = externalState.FinishedAtUtc ?? session.FinishedAtUtc;
        session.LastError = externalState.LastError;
        session.OutputModelPath = externalState.OutputModelPath;
    }

    private void ApplyRoundState(FLSession session, IReadOnlyList<ExternalRoundStatusDto> rounds)
    {
        foreach (var round in rounds)
        {
            if (round.RoundNumber <= 0)
                continue;

            var existing = session.Rounds.FirstOrDefault(r => r.RoundNumber == round.RoundNumber);
            if (existing is null)
            {
                existing = new FLRound
                {
                    Id = Guid.NewGuid(),
                    SessionId = session.Id,
                    RoundNumber = round.RoundNumber
                };
                // Explicitly mark as Added: when the parent session is Unchanged
                // and we attach a new dependent with a pre-generated Guid key, EF Core
                // defaults its state to Unchanged, which produces an UPDATE that
                // affects 0 rows and throws DbUpdateConcurrencyException at SaveChanges.
                _db.FLRounds.Add(existing);
                session.Rounds.Add(existing);
            }

            existing.AggregatedLoss = ToDecimalOrNull(round.AggregatedLoss);
            existing.AggregatedAccuracy = ToDecimalOrNull(round.AggregatedAccuracy);
            existing.AggregatedMacroF1 = ToDecimalOrNull(round.AggregatedMacroF1);
            existing.CompletedAtUtc = round.CompletedAtUtc;

            // Per-client communication cost (and per-client loss/accuracy) for this round.
            foreach (var update in round.ClientUpdates ?? [])
            {
                if (!Guid.TryParse(update.InstitutionId, out var institutionId))
                    continue;

                var modelUpdate = session.ModelUpdates.FirstOrDefault(
                    m => m.RoundNumber == round.RoundNumber && m.InstitutionId == institutionId);
                if (modelUpdate is null)
                {
                    modelUpdate = new FLModelUpdate
                    {
                        Id = Guid.NewGuid(),
                        SessionId = session.Id,
                        InstitutionId = institutionId,
                        RoundNumber = round.RoundNumber,
                        CreatedAtUtc = DateTime.UtcNow
                    };
                    _db.FLModelUpdates.Add(modelUpdate);
                    session.ModelUpdates.Add(modelUpdate);
                }

                modelUpdate.PayloadBytes = update.PayloadBytes;
                modelUpdate.TrainingLoss = ToDecimalOrNull(update.TrainingLoss) ?? modelUpdate.TrainingLoss;
                modelUpdate.ValidationAccuracy = ToDecimalOrNull(update.ValidationAccuracy) ?? modelUpdate.ValidationAccuracy;
            }
        }
    }

    private void ApplyParticipantState(FLSession session, IReadOnlyList<ExternalParticipantStatusDto> participants)
    {
        foreach (var participantState in participants)
        {
            if (!Guid.TryParse(participantState.InstitutionId, out var institutionId))
                continue;

            var existing = session.Participants.FirstOrDefault(p => p.InstitutionId == institutionId);
            if (existing is null)
            {
                existing = new FLParticipant
                {
                    Id = Guid.NewGuid(),
                    SessionId = session.Id,
                    InstitutionId = institutionId,
                    Status = FLParticipantStatus.Invited
                };
                _db.FLParticipants.Add(existing);
                session.Participants.Add(existing);
            }

            existing.Status = MapParticipantStatus(participantState.Status);
            existing.LastHeartbeatUtc = participantState.LastHeartbeatUtc ?? existing.LastHeartbeatUtc;
            if (!string.IsNullOrWhiteSpace(participantState.ClassHistogram))
                existing.ClassHistogramJson = participantState.ClassHistogram;
        }
    }

    private static FLSessionStatus MapSessionStatus(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "pending" => FLSessionStatus.Pending,
            "running" => FLSessionStatus.Running,
            "completed" => FLSessionStatus.Completed,
            "failed" => FLSessionStatus.Failed,
            "stopped" => FLSessionStatus.Stopped,
            _ => FLSessionStatus.Pending
        };

    private static FLParticipantStatus MapParticipantStatus(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "invited" => FLParticipantStatus.Invited,
            "accepted" => FLParticipantStatus.Accepted,
            "training" => FLParticipantStatus.Training,
            "completed" => FLParticipantStatus.Completed,
            "stopped" => FLParticipantStatus.Stopped,
            "failed" => FLParticipantStatus.Failed,
            _ => FLParticipantStatus.Invited
        };

    private async Task ApplyTimeoutPolicyAsync(
        FLSession session,
        string externalSessionId,
        CancellationToken cancellationToken)
    {
        if (session.Status is not (FLSessionStatus.Pending or FLSessionStatus.Running))
            return;
        if (session.StartedAtUtc is null)
            return;

        var timeoutMinutes = Math.Max(1, _options.SessionTimeoutMinutes);
        var deadlineUtc = session.StartedAtUtc.Value.AddMinutes(timeoutMinutes);
        if (DateTime.UtcNow <= deadlineUtc)
            return;

        _logger.LogWarning(
            "FL session {SessionId} exceeded timeout ({TimeoutMinutes} min). Stopping external session {ExternalSessionId}.",
            session.Id,
            timeoutMinutes,
            externalSessionId);

        var client = CreateControlPlaneClient();
        var response = await client.PostAsync($"sessions/{externalSessionId}/stop", null, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"FL control plane timeout-stop failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        session.Status = FLSessionStatus.Failed;
        session.FinishedAtUtc = DateTime.UtcNow;
        session.LastError = $"Session timed out after {timeoutMinutes} minutes.";
        foreach (var participant in session.Participants)
        {
            if (participant.Status is FLParticipantStatus.Invited or FLParticipantStatus.Accepted or FLParticipantStatus.Training)
                participant.Status = FLParticipantStatus.Failed;
            participant.LastHeartbeatUtc ??= DateTime.UtcNow;
        }
    }

    private async Task EnsureModelRegistryEntryForCompletedSessionAsync(
        FLSession session,
        ExternalSessionStatusResponse externalState,
        CancellationToken cancellationToken)
    {
        if (session.Status != FLSessionStatus.Completed || string.IsNullOrWhiteSpace(session.OutputModelPath))
            return;

        if (string.IsNullOrWhiteSpace(session.ExternalSessionId))
            throw new InvalidOperationException($"Completed FL session {session.Id} has no external session id.");

        if (IsExternalPlaceholderModelPath(session.OutputModelPath))
        {
            session.OutputModelPath = await EnsureModelArtifactUploadedAsync(
                session,
                session.ExternalSessionId!,
                externalState.OutputModelSha256,
                externalState.OutputModelSizeBytes,
                cancellationToken);
        }

        var outputNumClasses = externalState.OutputModelNumClasses.GetValueOrDefault(_options.OutputNumClasses);
        var outputImageSize = externalState.OutputModelImageSize.GetValueOrDefault(_options.OutputImageSize);
        var inputSpecJson = BuildOnnxInputSpecJson(outputImageSize);
        var outputSpecJson = BuildOnnxOutputSpecJson(outputNumClasses);

        var sessionTask = Truncate(session.ModelKey.Trim(), 100);
        var sessionVersion = $"fl_{session.Id:N}";
        var existing = await _db.AIModels
            .FirstOrDefaultAsync(m => m.Task == sessionTask && m.Version == sessionVersion, cancellationToken);

        var latestRoundAccuracy = session.Rounds
            .OrderByDescending(r => r.RoundNumber)
            .Select(r => r.AggregatedAccuracy)
            .FirstOrDefault();

        if (existing is not null)
        {
            existing.StorageFileName = Truncate(session.OutputModelPath!, 500);
            existing.Accuracy = latestRoundAccuracy ?? existing.Accuracy;
            existing.TrainingSource = "federated_learning";
            existing.TargetModality ??= await ResolveDefaultModalityForTaskAsync(sessionTask, cancellationToken);
            existing.NumClasses ??= outputNumClasses;
            existing.NumOutputClasses ??= outputNumClasses;
            existing.PreprocessingImageSize ??= outputImageSize;
            existing.PreprocessingMean ??= _options.OutputPreprocessingMean;
            existing.PreprocessingStd ??= _options.OutputPreprocessingStd;
            existing.OnnxInputSpec ??= inputSpecJson;
            existing.OnnxOutputSpec ??= outputSpecJson;
            return;
        }

        var defaultModality = await ResolveDefaultModalityForTaskAsync(sessionTask, cancellationToken);
        var modelName = Truncate($"{sessionTask} FL {session.CreatedAtUtc:yyyyMMdd_HHmm}", 256);

        var model = new AIModel
        {
            Id = Guid.NewGuid(),
            Name = modelName,
            Description = $"Federated model generated from FL session {session.Id}.",
            Task = sessionTask,
            Version = sessionVersion,
            StorageFileName = Truncate(session.OutputModelPath!, 500),
            Accuracy = latestRoundAccuracy,
            IsActive = false,
            TrainingSource = "federated_learning",
            TargetModality = defaultModality,
            NumClasses = outputNumClasses,
            NumOutputClasses = outputNumClasses,
            PreprocessingMethod = "imagenet_norm",
            PreprocessingImageSize = outputImageSize,
            PreprocessingMean = _options.OutputPreprocessingMean,
            PreprocessingStd = _options.OutputPreprocessingStd,
            OnnxInputSpec = inputSpecJson,
            OnnxOutputSpec = outputSpecJson,
            CreatedAt = DateTime.UtcNow
        };

        _db.AIModels.Add(model);
        _logger.LogInformation(
            "Published FL output to model registry: ModelId={ModelId}, SessionId={SessionId}, Task={Task}, Version={Version}",
            model.Id,
            session.Id,
            model.Task,
            model.Version);
    }

    private async Task<string> EnsureModelArtifactUploadedAsync(
        FLSession session,
        string externalSessionId,
        string? expectedSha256,
        long? expectedSizeBytes,
        CancellationToken cancellationToken)
    {
        var storageFileName = $"fl/{session.Id:N}/global.onnx";
        var expectedHash = NormalizeSha256(expectedSha256);
        if (!await _modelStorage.ModelExistsAsync(storageFileName, cancellationToken))
        {
            var artifact = await DownloadExternalModelArtifactAsync(externalSessionId, cancellationToken);
            ValidateArtifactMetadata(artifact.Bytes, artifact.Sha256, artifact.SizeBytes, expectedHash, expectedSizeBytes, session.Id);
            await using var stream = new MemoryStream(artifact.Bytes, writable: false);
            await _modelStorage.UploadModelAsync(storageFileName, stream, "application/octet-stream", cancellationToken);

            var existsAfterUpload = await _modelStorage.ModelExistsAsync(storageFileName, cancellationToken);
            if (!existsAfterUpload)
            {
                throw new InvalidOperationException(
                    $"FL model artifact upload failed for session {session.Id}. Object {storageFileName} not found after upload.");
            }
        }
        else if (expectedHash is not null || expectedSizeBytes.HasValue)
        {
            await using var existingStream = await _modelStorage.DownloadModelAsync(storageFileName, cancellationToken);
            if (expectedSizeBytes.HasValue && existingStream.CanSeek && existingStream.Length != expectedSizeBytes.Value)
            {
                throw new InvalidOperationException(
                    $"Stored FL model artifact size mismatch for session {session.Id}. " +
                    $"Expected {expectedSizeBytes.Value}, got {existingStream.Length}.");
            }
            var actualHash = await ComputeSha256HexAsync(existingStream, cancellationToken);
            if (expectedHash is not null && !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Stored FL model artifact hash mismatch for session {session.Id}. " +
                    $"Expected {expectedHash}, got {actualHash}.");
            }
        }

        return storageFileName;
    }

    private async Task<ExternalArtifactPayload> DownloadExternalModelArtifactAsync(
        string externalSessionId,
        CancellationToken cancellationToken)
    {
        var client = CreateControlPlaneClient();
        var response = await client.GetAsync($"sessions/{externalSessionId}/artifact", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"FL artifact download failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        var artifactBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var headerSha = response.Headers.TryGetValues("X-Artifact-Sha256", out var shaValues)
            ? shaValues.FirstOrDefault()
            : null;
        var headerSize = response.Headers.TryGetValues("X-Artifact-Size", out var sizeValues)
            && long.TryParse(sizeValues.FirstOrDefault(), out var parsedSize)
            ? parsedSize
            : (long?)null;

        return new ExternalArtifactPayload(artifactBytes, headerSha, headerSize);
    }

    private static void ValidateArtifactMetadata(
        byte[] bytes,
        string? artifactShaHeader,
        long? artifactSizeHeader,
        string? expectedSha,
        long? expectedSize,
        Guid sessionId)
    {
        if (expectedSize.HasValue && expectedSize.Value != bytes.LongLength)
        {
            throw new InvalidOperationException(
                $"FL artifact size mismatch for session {sessionId}. Expected {expectedSize.Value}, got {bytes.LongLength}.");
        }
        if (artifactSizeHeader.HasValue && artifactSizeHeader.Value != bytes.LongLength)
        {
            throw new InvalidOperationException(
                $"FL artifact header size mismatch for session {sessionId}. Header {artifactSizeHeader.Value}, bytes {bytes.LongLength}.");
        }

        var computedSha = ComputeSha256Hex(bytes);
        var normalizedHeaderSha = NormalizeSha256(artifactShaHeader);
        if (normalizedHeaderSha is not null && !string.Equals(computedSha, normalizedHeaderSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"FL artifact SHA mismatch for session {sessionId}. Header {normalizedHeaderSha}, computed {computedSha}.");
        }
        if (expectedSha is not null && !string.Equals(computedSha, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"FL artifact expected SHA mismatch for session {sessionId}. Expected {expectedSha}, computed {computedSha}.");
        }
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> ComputeSha256HexAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
            stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeSha256(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
            return null;
        var normalized = sha.Trim().ToLowerInvariant();
        return normalized.Length == 64 ? normalized : null;
    }

    private static bool IsExternalPlaceholderModelPath(string? outputModelPath) =>
        !string.IsNullOrWhiteSpace(outputModelPath) &&
        outputModelPath.StartsWith("fl-models/", StringComparison.OrdinalIgnoreCase);

    private static string BuildOnnxInputSpecJson(int outputImageSize)
    {
        var size = Math.Max(1, outputImageSize);
        return JsonSerializer.Serialize(new[]
        {
            new
            {
                name = "input",
                shape = new[] { 1, 3, size, size },
                dtype = "float32"
            }
        });
    }

    private static string BuildOnnxOutputSpecJson(int outputNumClasses)
    {
        var classes = Math.Max(2, outputNumClasses);
        return JsonSerializer.Serialize(new[]
        {
            new
            {
                name = "logits",
                shape = new[] { 1, classes },
                dtype = "float32"
            }
        });
    }

    private async Task<string?> ResolveDefaultModalityForTaskAsync(string task, CancellationToken cancellationToken)
    {
        return await _db.AIModels
            .AsNoTracking()
            .Where(m => m.Task == task && !string.IsNullOrWhiteSpace(m.TargetModality))
            .OrderByDescending(m => m.IsActive)
            .ThenByDescending(m => m.CreatedAt)
            .Select(m => m.TargetModality)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed record ExternalArtifactPayload(
        byte[] Bytes,
        string? Sha256,
        long? SizeBytes);

    private sealed record StartExternalSessionRequest(
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("model_key")] string ModelKey,
        [property: JsonPropertyName("architecture")] string? Architecture,
        [property: JsonPropertyName("total_rounds")] int TotalRounds,
        [property: JsonPropertyName("institution_ids")] List<string> InstitutionIds,
        [property: JsonPropertyName("min_available_clients")] int MinAvailableClients,
        [property: JsonPropertyName("min_fit_clients")] int MinFitClients,
        [property: JsonPropertyName("min_evaluate_clients")] int MinEvaluateClients);

    private sealed record ExternalClientUpdateDto(
        [property: JsonPropertyName("institution_id")] string InstitutionId,
        [property: JsonPropertyName("payload_bytes")] long? PayloadBytes,
        [property: JsonPropertyName("training_loss")] double? TrainingLoss,
        [property: JsonPropertyName("validation_accuracy")] double? ValidationAccuracy);

    private sealed record ExternalRoundStatusDto(
        [property: JsonPropertyName("round_number")] int RoundNumber,
        [property: JsonPropertyName("aggregated_loss")] double? AggregatedLoss,
        [property: JsonPropertyName("aggregated_accuracy")] double? AggregatedAccuracy,
        [property: JsonPropertyName("aggregated_macro_f1")] double? AggregatedMacroF1,
        [property: JsonPropertyName("completed_at_utc")] DateTime? CompletedAtUtc,
        [property: JsonPropertyName("client_updates")] List<ExternalClientUpdateDto>? ClientUpdates);

    private sealed record ExternalParticipantStatusDto(
        [property: JsonPropertyName("institution_id")] string InstitutionId,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("last_heartbeat_utc")] DateTime? LastHeartbeatUtc,
        [property: JsonPropertyName("class_histogram")] string? ClassHistogram);

    private sealed record ExternalClientStatusDto(
        [property: JsonPropertyName("institution_id")] string InstitutionId,
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("last_heartbeat_utc")] DateTime? LastHeartbeatUtc,
        [property: JsonPropertyName("is_online")] bool IsOnline);

    private sealed record ExternalClientsResponse(
        [property: JsonPropertyName("clients")] List<ExternalClientStatusDto> Clients);

    private sealed record ExternalSessionStatusResponse(
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("model_key")] string ModelKey,
        [property: JsonPropertyName("architecture")] string? Architecture,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("total_rounds")] int TotalRounds,
        [property: JsonPropertyName("current_round")] int CurrentRound,
        [property: JsonPropertyName("started_at_utc")] DateTime? StartedAtUtc,
        [property: JsonPropertyName("finished_at_utc")] DateTime? FinishedAtUtc,
        [property: JsonPropertyName("last_error")] string? LastError,
        [property: JsonPropertyName("output_model_path")] string? OutputModelPath,
        [property: JsonPropertyName("output_model_sha256")] string? OutputModelSha256,
        [property: JsonPropertyName("output_model_size_bytes")] long? OutputModelSizeBytes,
        [property: JsonPropertyName("output_model_num_classes")] int? OutputModelNumClasses,
        [property: JsonPropertyName("output_model_image_size")] int? OutputModelImageSize,
        [property: JsonPropertyName("rounds")] List<ExternalRoundStatusDto> Rounds,
        [property: JsonPropertyName("participants")] List<ExternalParticipantStatusDto> Participants);
}
