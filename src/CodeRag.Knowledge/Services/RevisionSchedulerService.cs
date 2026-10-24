using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Knowledge.Services;

public sealed class RevisionSchedulerService : BackgroundService
{
    private readonly RevisionService _revision;
    private readonly IOptionsMonitor<KnowledgeOptions> _options;
    private readonly ILogger<RevisionSchedulerService> _logger;

    public RevisionSchedulerService(
        RevisionService revision,
        IOptionsMonitor<KnowledgeOptions> options,
        ILogger<RevisionSchedulerService> logger)
    {
        _revision = revision;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = _options.CurrentValue.Revision;
                if (options.Enabled && await _revision.IsDueAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("Scheduled knowledge revision is due");
                    var result = await _revision.RunAsync(trigger: "scheduled", ct: stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Scheduled knowledge revision finished: scanned={Scanned} created={Created} skipped={Skipped}",
                        result.Scanned, result.JobsCreated, result.JobsSkipped);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled knowledge revision failed; will retry on the next check");
            }

            var minutes = Math.Clamp(_options.CurrentValue.Revision.CheckMinutes, 1, 1440);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
