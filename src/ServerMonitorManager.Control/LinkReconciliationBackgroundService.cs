using Microsoft.Extensions.Options;

namespace ServerMonitorManager.Control;

public sealed class LinkReconciliationBackgroundService(
    LinkService links,
    ILinkPolicyApplier applier,
    IOptions<ControlOptions> options,
    TimeProvider timeProvider,
    ILogger<LinkReconciliationBackgroundService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private DateTimeOffset? _nextRegularAt;
    private DateTimeOffset? _backoffUntil;
    private int _unavailableAttempts;

    internal async Task<LinkFullReconciliationResult?> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        await _passGate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            string? requestGeneration = null;
            try
            {
                requestGeneration = await applier.GetReconciliationRequestAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not inspect the Link reconciliation marker.");
            }
            if (_backoffUntil is not null && now < _backoffUntil)
            {
                return null;
            }
            if (requestGeneration is null && _nextRegularAt is not null && now < _nextRegularAt)
            {
                return null;
            }

            var result = await links.ReconcileAllAsync(cancellationToken);
            var interval = TimeSpan.FromSeconds(options.Value.LinkReconciliationSeconds);
            if (result.FirewallUnavailable)
            {
                _unavailableAttempts = Math.Min(_unavailableAttempts + 1, 4);
                _backoffUntil = now + TimeSpan.FromTicks(interval.Ticks * _unavailableAttempts);
            }
            else
            {
                _unavailableAttempts = 0;
                _backoffUntil = null;
                _nextRegularAt = now + interval;
                if (requestGeneration is not null && result.Failed == 0)
                {
                    await applier.CompleteReconciliationAsync(requestGeneration, cancellationToken);
                }
            }
            return result;
        }
        finally
        {
            _passGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = Math.Min(options.Value.LinkReconciliationSeconds, 30);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(pollSeconds), timeProvider);
        do
        {
            try
            {
                var result = await RunOnceAsync(stoppingToken);
                if (result is not null)
                {
                    logger.LogInformation(
                        "Link reconciliation completed: {Examined} examined, {Failed} failed, firewall unavailable: {Unavailable}.",
                        result.Examined,
                        result.Failed,
                        result.FirewallUnavailable);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Link reconciliation failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
