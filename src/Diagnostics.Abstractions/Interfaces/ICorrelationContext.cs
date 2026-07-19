namespace Diagnostics.Abstractions.Interfaces;

/// <summary>
/// Ambient (AsyncLocal-backed) context carrying the current correlation id, the enclosing transaction id/parent id (span tree), and the current functional area (category).
/// A line log written inside a <see cref="ITransactionLogger.BeginTransaction"/> scope is stamped automatically with the scope's transaction id and category — see §6 of the design doc.
/// </summary>
public interface ICorrelationContext
{
    /// <summary>
    /// Correlation id for the current logical operation (request). Read from the inbound X-Correlation-ID header if present, otherwise generated. Defaults to a fresh guid if nothing has set it yet (e.g. outside any HTTP request, such as a background job).
    /// </summary>
    Guid CorrelationId { get; }

    /// <summary>
    /// Id of the innermost open transaction scope, or null if none is open.
    /// </summary>
    Guid? CurrentTransactionId { get; }

    /// <summary>
    /// Id of the transaction that opened the current one, or null (root scope).
    /// </summary>
    Guid? CurrentParentTransactionId { get; }

    /// <summary>
    /// Functional area of the innermost open transaction scope (e.g. "UploadPdf"). Falls back to the reserved <see cref="CategoryNames.None"/> guardrail value when no scope is open, so line logs written outside any transaction still satisfy the NOT NULL category column while being easy to spot as un-categorized.
    /// </summary>
    string CurrentCategory { get; }

    /// <summary>
    /// User of the innermost open transaction scope, or null if none has been set.
    /// </summary>
    string? CurrentUser { get; }

    /// <summary>
    /// Sets the correlation id for the current async flow (e.g. from an inbound header). Intended to be called once, near the start of request processing.
    /// </summary>
    void SetCorrelationId(Guid correlationId);

    /// <summary>
    /// Sets the user for the current async flow.
    /// Used by <see cref="ITransactionScope.SetUser"/> so line logs written inside the scope inherit it.
    /// Application code does not call this directly.
    /// </summary>
    void SetUser(string? user);

    /// <summary>
    /// Opens a new ambient transaction scope (sets current transaction id/parent id/category for the current async flow) and returns a disposable that restores the previous ambient state.
    /// Used by <see cref="ITransactionScope"/> implementations — application code does not call this directly.
    /// </summary>
    IDisposable PushTransaction(Guid transactionId, Guid? parentTransactionId, string category);
}
