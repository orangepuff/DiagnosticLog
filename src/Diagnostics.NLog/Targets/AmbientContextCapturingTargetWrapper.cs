using Diagnostics.Abstractions.Interfaces;
using NLog.Common;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace Diagnostics.NLog.Targets;

/// <summary>
/// Sits between a logging rule and its wrapped target (typically an <c>AsyncTargetWrapper</c>), snapshotting ambient correlation/transaction/category state onto the event before forwarding it.
/// The wrapped target may defer processing to its own background thread, where the caller's <c>AsyncLocal</c>-backed ambient state no longer exists.
/// <c>WrapperTargetBase</c> calls <see cref="Write(AsyncLogEventInfo)"/> synchronously, on the caller's own thread, before any such deferral — that is why the snapshot is taken here rather than in the wrapped target's own <c>Write</c>.
/// </summary>
[Target("AmbientContextCapture", IsWrapper = true)]
public sealed class AmbientContextCapturingTargetWrapper : WrapperTargetBase
{
    private readonly ICorrelationContext _correlationContext;

    public AmbientContextCapturingTargetWrapper(ICorrelationContext correlationContext)
    {
        _correlationContext = correlationContext;
    }

    protected override void Write(AsyncLogEventInfo logEvent)
    {
        logEvent.LogEvent.Properties[LogsTarget.CorrelationIdPropertyKey] = _correlationContext.CorrelationId;
        logEvent.LogEvent.Properties[LogsTarget.TransactionIdPropertyKey] = _correlationContext.CurrentTransactionId;
        logEvent.LogEvent.Properties[LogsTarget.CategoryPropertyKey] = _correlationContext.CurrentCategory;
        logEvent.LogEvent.Properties[LogsTarget.UserPropertyKey] = _correlationContext.CurrentUser;

        WrappedTarget.WriteAsyncLogEvent(logEvent);
    }
}
