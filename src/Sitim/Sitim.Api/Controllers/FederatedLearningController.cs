using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sitim.Api.Security;
using Sitim.Core.Contracts.FL;
using Sitim.Core.Entities;
using Sitim.Core.Services;
using System.Security.Claims;

namespace Sitim.Api.Controllers;

[Authorize(Roles = SitimRoles.PlatformAdmin)]
[ApiController]
[Route("api/fl")]
public sealed class FederatedLearningController : ControllerBase
{
    private readonly IFLOrchestrationService _flOrchestrationService;

    public FederatedLearningController(IFLOrchestrationService flOrchestrationService)
    {
        _flOrchestrationService = flOrchestrationService;
    }

    [HttpPost("sessions")]
    public async Task<ActionResult<FLSessionDto>> StartSession(
        [FromBody] StartFLSessionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        try
        {
            var session = await _flOrchestrationService.StartSessionAsync(
                request.ModelKey,
                request.TotalRounds,
                request.InstitutionIds,
                request.Architecture,
                userId,
                cancellationToken);

            return CreatedAtAction(nameof(GetSessionById), new { id = session.Id }, MapSession(session));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("sessions")]
    public async Task<ActionResult<IReadOnlyList<FLSessionDto>>> ListSessions(CancellationToken cancellationToken)
    {
        var sessions = await _flOrchestrationService.ListSessionsAsync(cancellationToken);
        return Ok(sessions.Select(MapSession).ToList());
    }

    [HttpGet("clients")]
    public async Task<ActionResult<IReadOnlyList<FLConnectedClientDto>>> ListConnectedClients(CancellationToken cancellationToken)
    {
        var clients = await _flOrchestrationService.GetAvailableClientsAsync(cancellationToken);
        return Ok(clients.Select(c => new FLConnectedClientDto(
            c.InstitutionId,
            c.ClientId,
            c.Status,
            c.LastHeartbeatUtc,
            c.IsOnline)).ToList());
    }

    [HttpGet("sessions/{id:guid}")]
    public async Task<ActionResult<FLSessionDetailsDto>> GetSessionById(Guid id, CancellationToken cancellationToken)
    {
        var session = await _flOrchestrationService.GetSessionAsync(id, cancellationToken);
        if (session == null)
            return NotFound(new { error = "FL session not found" });

        return Ok(MapSessionDetails(session));
    }

    [HttpPost("sessions/{id:guid}/stop")]
    public async Task<ActionResult<object>> StopSession(Guid id, CancellationToken cancellationToken)
    {
        var stopped = await _flOrchestrationService.StopSessionAsync(id, cancellationToken);
        if (!stopped)
            return NotFound(new { error = "FL session not found" });

        return Ok(new { message = "FL session stopped", sessionId = id });
    }

    [HttpGet("sessions/{id:guid}/model")]
    public async Task<ActionResult<object>> GetSessionModel(Guid id, CancellationToken cancellationToken)
    {
        var session = await _flOrchestrationService.GetSessionAsync(id, cancellationToken);
        if (session == null)
            return NotFound(new { error = "FL session not found" });

        if (string.IsNullOrWhiteSpace(session.OutputModelPath))
            return NotFound(new { error = "FL output model not available for this session yet" });

        return Ok(new
        {
            sessionId = session.Id,
            modelPath = session.OutputModelPath,
            status = session.Status.ToString()
        });
    }

    [HttpGet("sessions/{id:guid}/published-model")]
    public async Task<ActionResult<FLPublishedModelDto>> GetPublishedModel(Guid id, CancellationToken cancellationToken)
    {
        var publishedModel = await _flOrchestrationService.GetPublishedModelForSessionAsync(id, cancellationToken);
        if (publishedModel is null)
            return NotFound(new { error = "No published model found for this FL session" });

        return Ok(new FLPublishedModelDto(
            publishedModel.ModelId,
            publishedModel.Name,
            publishedModel.Task,
            publishedModel.Version,
            publishedModel.StorageFileName,
            publishedModel.IsActive));
    }

    [HttpPost("sessions/{id:guid}/activate-model")]
    public async Task<ActionResult<FLPublishedModelDto>> ActivatePublishedModel(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var publishedModel = await _flOrchestrationService.ActivateSessionModelAsync(id, cancellationToken);
            return Ok(new FLPublishedModelDto(
                publishedModel.ModelId,
                publishedModel.Name,
                publishedModel.Task,
                publishedModel.Version,
                publishedModel.StorageFileName,
                publishedModel.IsActive));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private bool TryGetUserId(out Guid userId)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdClaim, out userId);
    }

    private static FLSessionDto MapSession(FLSession session) =>
        new(
            session.Id,
            session.ModelKey,
            session.Architecture,
            session.Status.ToString(),
            session.TotalRounds,
            session.CurrentRound,
            session.Participants.Count,
            session.CreatedAtUtc,
            session.StartedAtUtc,
            session.FinishedAtUtc);

    private static FLSessionDetailsDto MapSessionDetails(FLSession session)
    {
        // Per-round communication cost = sum of client payloads reported that round.
        var payloadByRound = session.ModelUpdates
            .Where(m => m.PayloadBytes.HasValue)
            .GroupBy(m => m.RoundNumber)
            .ToDictionary(g => g.Key, g => g.Sum(m => m.PayloadBytes!.Value));

        var updatesWithPayload = session.ModelUpdates.Count(m => m.PayloadBytes.HasValue);
        long totalCommunicationBytes = session.ModelUpdates
            .Where(m => m.PayloadBytes.HasValue)
            .Sum(m => m.PayloadBytes!.Value);
        double avgPayloadPerClientPerRound = updatesWithPayload > 0
            ? (double)totalCommunicationBytes / updatesWithPayload
            : 0d;

        return new(
            session.Id,
            session.ModelKey,
            session.Architecture,
            session.Status.ToString(),
            session.TotalRounds,
            session.CurrentRound,
            session.CreatedByUserId,
            session.CreatedAtUtc,
            session.StartedAtUtc,
            session.FinishedAtUtc,
            session.LastError,
            session.OutputModelPath,
            session.Participants
                .OrderBy(p => p.Institution.Name)
                .Select(p => new FLParticipantDto(
                    p.InstitutionId,
                    p.Institution.Name,
                    p.Status.ToString(),
                    p.LastHeartbeatUtc,
                    p.ClassHistogramJson))
                .ToList(),
            session.Rounds
                .OrderBy(r => r.RoundNumber)
                .Select(r => new FLRoundDto(
                    r.RoundNumber,
                    r.AggregatedLoss,
                    r.AggregatedAccuracy,
                    r.AggregatedMacroF1,
                    payloadByRound.TryGetValue(r.RoundNumber, out var bytes) ? bytes : null,
                    r.CompletedAtUtc))
                .ToList(),
            totalCommunicationBytes,
            avgPayloadPerClientPerRound);
    }
}


