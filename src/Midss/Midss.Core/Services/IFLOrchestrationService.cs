using Midss.Core.Entities;

namespace Midss.Core.Services;

public interface IFLOrchestrationService
{
    Task<FLSession> StartSessionAsync(
        string modelKey,
        int totalRounds,
        IReadOnlyCollection<Guid> institutionIds,
        string? architecture,
        Guid createdByUserId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FLSession>> ListSessionsAsync(CancellationToken cancellationToken);

    Task<FLSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<bool> StopSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FLConnectedClient>> GetAvailableClientsAsync(CancellationToken cancellationToken);

    Task<int> RefreshRunningSessionsAsync(CancellationToken cancellationToken);

    Task<FLPublishedModel?> GetPublishedModelForSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<FLPublishedModel> ActivateSessionModelAsync(Guid sessionId, CancellationToken cancellationToken);
}

public sealed record FLConnectedClient(
    Guid InstitutionId,
    string ClientId,
    string Status,
    DateTime? LastHeartbeatUtc,
    bool IsOnline);

public sealed record FLPublishedModel(
    Guid ModelId,
    string Name,
    string Task,
    string Version,
    string StorageFileName,
    bool IsActive);
