using Hangfire;
using Sitim.Core.Services;

namespace Sitim.Api.HangfireJobs
{
    /// <summary>
    /// Hangfire entry-point for executing an analysis.
    /// Keeping this in the API project allows Hangfire to resolve it via DI.
    /// </summary>
    public sealed class AnalysisHangfireJob
    {
        private readonly IAnalysisService _analysis;

        public AnalysisHangfireJob(IAnalysisService analysis)
        {
            _analysis = analysis;
        }

        [AutomaticRetry(Attempts = 3, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
        public Task RunAsync(Guid analysisJobId)
            => _analysis.RunAsync(analysisJobId, CancellationToken.None);
    }
}
