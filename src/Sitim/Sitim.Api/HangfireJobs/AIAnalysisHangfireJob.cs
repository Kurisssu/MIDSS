using Hangfire;
using Sitim.Core.Services;

namespace Sitim.Api.HangfireJobs
{
    /// <summary>
    /// Hangfire background job executor for AI analysis.
    /// Kept in the API project to leverage Hangfire's DI container resolution.
    /// Executes the analysis workflow and updates job record with results/status.
    /// </summary>
    public sealed class AIAnalysisHangfireJob
    {
        private readonly IAIInferenceService _inference;
        private readonly ILogger<AIAnalysisHangfireJob> _logger;

        public AIAnalysisHangfireJob(IAIInferenceService inference, ILogger<AIAnalysisHangfireJob> logger)
        {
            _inference = inference;
            _logger = logger;
        }

        /// <summary>
        /// Entry point for Hangfire. Executes AI analysis for a job record.
        /// </summary>
        [AutomaticRetry(Attempts = 3, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
        public async Task RunAsync(Guid analysisJobId)
        {
            _logger.LogInformation("Starting Hangfire execution for AI analysis job {JobId}", analysisJobId);
            await _inference.ExecuteAnalysisJobAsync(analysisJobId, CancellationToken.None);
        }
    }
}
