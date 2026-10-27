using Hangfire;
using Microsoft.EntityFrameworkCore;
using Sitim.Api.HangfireJobs;
using Sitim.Infrastructure.Data;

namespace Sitim.Api.Services
{
    /// <summary>
    /// Schedules AI analysis jobs for execution via Hangfire background queue.
    /// Ensures job records are persisted with Hangfire job ID for tracking.
    /// </summary>
    public class AIAnalysisJobScheduler
    {
        private readonly IBackgroundJobClient _jobs;
        private readonly AppDbContext _db;
        private readonly ILogger<AIAnalysisJobScheduler> _logger;

        public AIAnalysisJobScheduler(IBackgroundJobClient jobs, AppDbContext db, ILogger<AIAnalysisJobScheduler> logger)
        {
            _jobs = jobs;
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Enqueues an AI analysis job for background execution.
        /// Updates the job record with Hangfire job ID and status = Queued.
        /// </summary>
        public async Task<string> EnqueueAsync(Guid analysisJobId, CancellationToken ct)
        {
            var job = await _db.AIAnalysisJobs.FirstOrDefaultAsync(x => x.Id == analysisJobId, ct);
            if (job is null)
                throw new InvalidOperationException($"AI analysis job not found: {analysisJobId}");

            // Enqueue background job with Hangfire
            var hangfireJobId = _jobs.Enqueue<AIAnalysisHangfireJob>(x => x.RunAsync(analysisJobId));

            _logger.LogInformation(
                "Enqueued AI analysis job {AnalysisJobId} with Hangfire job ID {HangfireJobId}",
                analysisJobId, hangfireJobId);

            // Update job record with Hangfire reference and status
            job.HangfireJobId = hangfireJobId;
            job.Status = "Queued";
            job.ErrorMessage = null;
            job.StartedAtUtc = null;
            job.FinishedAtUtc = null;

            await _db.SaveChangesAsync(ct);

            return hangfireJobId;
        }
    }
}
