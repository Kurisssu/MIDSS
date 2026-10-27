namespace Sitim.Core.Services
{
    /// <summary>
    /// Executes a single analysis job (download data, run pipeline, persist output).
    /// Scheduling is handled separately (Hangfire).
    /// </summary>
    public interface IAnalysisService
    {
        Task RunAsync(Guid analysisJobId, CancellationToken ct);
    }
}
