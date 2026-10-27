using Hangfire;
using Sitim.Core.Services;

namespace Sitim.Api.HangfireJobs;

public sealed class FLSessionMonitorHangfireJob
{
    private readonly IFLOrchestrationService _flOrchestrationService;
    private readonly ILogger<FLSessionMonitorHangfireJob> _logger;

    public FLSessionMonitorHangfireJob(
        IFLOrchestrationService flOrchestrationService,
        ILogger<FLSessionMonitorHangfireJob> logger)
    {
        _flOrchestrationService = flOrchestrationService;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 3, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync()
    {
        var refreshedCount = await _flOrchestrationService.RefreshRunningSessionsAsync(CancellationToken.None);
        _logger.LogInformation("FL monitor refreshed {SessionCount} active session(s).", refreshedCount);
    }
}
