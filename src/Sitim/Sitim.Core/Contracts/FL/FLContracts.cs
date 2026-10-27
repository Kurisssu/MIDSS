namespace Sitim.Core.Contracts.FL;

/// <summary>Body for POST /api/fl/sessions (start new FL session).</summary>
public sealed record StartFLSessionRequest(
    string ModelKey,
    int TotalRounds,
    List<Guid> InstitutionIds,
    /// <summary>Network to train, e.g. "resnet18" / "resnet50". Null keeps the control plane default.</summary>
    string? Architecture = null);

/// <summary>Compact FL session row — listing on the Federated Learning dashboard.</summary>
public sealed record FLSessionDto(
    Guid Id,
    string ModelKey,
    string? Architecture,
    string Status,
    int TotalRounds,
    int CurrentRound,
    int ParticipantsCount,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc);

/// <summary>Participant entry in an FL session — one row per invited institution.</summary>
public sealed record FLParticipantDto(
    Guid InstitutionId,
    string InstitutionName,
    string Status,
    DateTime? LastHeartbeatUtc,
    /// <summary>Per-class sample counts as JSON ({"0":120,...}); null if not reported yet.</summary>
    string? ClassHistogramJson);

/// <summary>Connected FL client (heartbeat from a Flower fl-client container).</summary>
public sealed record FLConnectedClientDto(
    Guid InstitutionId,
    string ClientId,
    string Status,
    DateTime? LastHeartbeatUtc,
    bool IsOnline);

/// <summary>One round of FL aggregation — metrics produced by Flower's aggregate_evaluate.</summary>
public sealed record FLRoundDto(
    int RoundNumber,
    decimal? AggregatedLoss,
    decimal? AggregatedAccuracy,
    decimal? AggregatedMacroF1,
    /// <summary>Total bytes transmitted by all clients in this round (communication cost).</summary>
    long? RoundPayloadBytes,
    DateTime? CompletedAtUtc);

/// <summary>Detailed FL session view — includes participants + per-round metrics.</summary>
public sealed record FLSessionDetailsDto(
    Guid Id,
    string ModelKey,
    string? Architecture,
    string Status,
    int TotalRounds,
    int CurrentRound,
    Guid CreatedByUserId,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc,
    string? LastError,
    string? OutputModelPath,
    List<FLParticipantDto> Participants,
    List<FLRoundDto> Rounds,
    /// <summary>Total communication cost of the session in bytes (sum over all client updates).</summary>
    long TotalCommunicationBytes,
    /// <summary>Average bytes per client per round (total / number of client updates).</summary>
    double AvgPayloadBytesPerClientPerRound);

/// <summary>FL session's published model in the AI model registry (post-completion).</summary>
public sealed record FLPublishedModelDto(
    Guid ModelId,
    string Name,
    string Task,
    string Version,
    string StorageFileName,
    bool IsActive);
