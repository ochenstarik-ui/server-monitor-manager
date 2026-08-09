using Microsoft.Extensions.Options;

namespace ServerMonitorManager.Control;

public sealed class LinkReconciliationBackgroundService(
    LinkService links,
    ILinkPolicyApplier applier,
    IOptions<ControlOptions> options,
    TimeProvider timeProvider,
    ILogger<LinkReconciliationBackgroundService> logger) : BackgroundService
{
    private const int PromptAttemptLimit = 3;
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private DateTimeOffset? _nextRegularAt;
    private DateTimeOffset? _backoffUntil;
    private int _unavailableAttempts;
    private int _promptFailureAttempts;
    private bool _promptThrottleWarningLogged;

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
            var promptBypassesThrottle = requestGeneration is not null
                && _promptFailureAttempts < PromptAttemptLimit;
            if (!promptBypassesThrottle && _nextRegularAt is not null && now < _nextRegularAt)
            {
                return null;
            }

            var interval = TimeSpan.FromSeconds(options.Value.LinkReconciliationSeconds);
            _nextRegularAt = now + interval;
            LinkFullReconciliationResult result;
            try
            {
                result = await links.ReconcileAllAsync(cancellationToken);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && requestGeneration is not null)
            {
                RegisterPromptFailure([]);
                throw;
            }
            if (result.FirewallUnavailable)
            {
                _unavailableAttempts = Math.Min(_unavailableAttempts + 1, 4);
                _backoffUntil = now + TimeSpan.FromTicks(interval.Ticks * _unavailableAttempts);
                return result;
            }

            _unavailableAttempts = 0;
            _backoffUntil = null;
            if (requestGeneration is null)
            {
                return result;
            }

            if (result.Failed > 0)
            {
                RegisterPromptFailure(result.FailedPolicyIds);
                return result;
            }

            try
            {
                await applier.CompleteReconciliationAsync(requestGeneration, cancellationToken);
                _promptFailureAttempts = 0;
                _promptThrottleWarningLogged = false;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Link reconciliation completed, but marker generation {Generation} could not be consumed.",
                    requestGeneration);
                RegisterPromptFailure(["marker-completion"]);
            }
            return result;
        }
        finally
        {
            _passGate.Release();
        }
    }

    private void RegisterPromptFailure(IReadOnlyList<string> failedPolicyIds)
    {
        _promptFailureAttempts = Math.Min(_promptFailureAttempts + 1, PromptAttemptLimit);
        if (_promptFailureAttempts == PromptAttemptLimit && !_promptThrottleWarningLogged)
        {
            _promptThrottleWarningLogged = true;
            logger.LogWarning(
                "Link reconciliation marker prompt exhausted after {Attempts} failures; regular throttle restored. Failed policy IDs: {FailedPolicyIds}.",
                PromptAttemptLimit,
                failedPolicyIds.Count == 0 ? "unknown" : string.Join(",", failedPolicyIds));
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
                        "Link reconciliation completed: {Examined} examined, {Converged} converged, {Deferred} deferred, {Failed} failed, firewall unavailable: {Unavailable}. Deferred policy IDs: {DeferredPolicyIds}.",
                        result.Examined,
                        result.Converged,
                        result.Deferred,
                        result.Failed,
                        result.FirewallUnavailable,
                        result.DeferredPolicyIds.Count == 0
                            ? "none"
                            : string.Join(",", result.DeferredPolicyIds));
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
