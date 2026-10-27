namespace Midss.Api.Services
{
    public interface IAnalysisJobScheduler
    {
        Task<string> EnqueueAsync(Guid analysisJobId, CancellationToken ct);
    }
}
