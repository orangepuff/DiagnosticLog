using Diagnostics.Abstractions;
using Diagnostics.Abstractions.Interfaces;
using Diagnostics.NLog.Targets;
using Microsoft.Extensions.Hosting;
using NLog;
using NLog.Config;
using NLog.Targets.Wrappers;

namespace Diagnostics.NLog.Configuration;

/// <summary>
/// Applies this app's NLog rules from <c>[dbo].[Configurations]</c> at startup, then polls <c>dtUpdatedTime</c> so ops can retune levels/rules centrally without a re-deployment (design doc §5 — "Hot reload: poll dtUpdatedTime ... reapply when it changes").
/// Both custom targets are wrapped in NLog's <see cref="AsyncTargetWrapper"/> so a logging call never blocks the caller, even before it reaches the target's own bounded batch writer (§3/§7).
/// <see cref="LogsTarget"/> is additionally wrapped in <see cref="AmbientContextCapturingTargetWrapper"/>, on the near (non-deferred) side of the async wrapper, so the caller's ambient correlation/transaction/category state is snapshotted before it is lost to <see cref="AsyncTargetWrapper"/>'s background drain thread.
/// </summary>
public sealed class DiagnosticsConfigHostedService(
    DbConfigProvider configProvider,
    LogsTarget logsTarget,
    TransactionsTarget transactionsTarget,
    ICorrelationContext correlationContext,
    DiagnosticsOptions options) : BackgroundService
{
    private DateTime? _lastAppliedUpdatedTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Transactions bypass NLog's rule dispatch entirely (see TransactionsTarget), so it just needs its own pipeline started once; it does not need to be part of LogManager.Configuration.
        // An unhandled exception here would fault this BackgroundService and, by default hosting behavior, stop the whole app — exactly what §7 says logging must never do.
        try
        {
            transactionsTarget.EnsureStarted();
        }
        catch
        {
        }

        await ApplyIfChangedAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(options.ConfigPollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await ApplyIfChangedAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyIfChangedAsync(CancellationToken ct)
    {
        ConfigSnapshot snapshot;
        try
        {
            snapshot = await configProvider.LoadAsync(options.LoggerName, options.EnvironmentName, ct).ConfigureAwait(false);
        }
        catch
        {
            // Never let config polling take the app down (§7) — keep whatever config is live.
            return;
        }

        if (_lastAppliedUpdatedTime.HasValue
            && snapshot.UpdatedTime.HasValue
            && snapshot.UpdatedTime.Value <= _lastAppliedUpdatedTime.Value)
        {
            return; // unchanged since last apply
        }

        try
        {
            Apply(snapshot);
        }
        catch
        {
            // Never let a bad rule (unparseable min level, etc.) or a target initialization failure take the host down (§7) — keep whatever config was live before this poll.
            return;
        }

        _lastAppliedUpdatedTime = snapshot.UpdatedTime ?? _lastAppliedUpdatedTime ?? DateTime.UtcNow;
    }

    private void Apply(ConfigSnapshot snapshot)
    {
        var config = new LoggingConfiguration();

        var asyncLogsTarget = new AsyncTargetWrapper(logsTarget)
        {
            Name = "DiagnosticsLogsAsync",
            OverflowAction = AsyncTargetWrapperOverflowAction.Discard,
        };

        var capturingLogsTarget = new AmbientContextCapturingTargetWrapper(correlationContext)
        {
            Name = "DiagnosticsLogsCapture",
            WrappedTarget = asyncLogsTarget,
        };

        config.AddTarget(capturingLogsTarget);

        foreach (var rule in snapshot.Rules)
        {
            var minLevel = LogLevel.FromString(rule.MinLevel);
            config.AddRule(minLevel, LogLevel.Fatal, capturingLogsTarget, rule.LoggerNamePattern);
        }

        LogManager.Configuration = config;
        LogManager.ReconfigExistingLoggers();
    }
}
