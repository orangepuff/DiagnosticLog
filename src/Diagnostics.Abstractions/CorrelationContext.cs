using Diagnostics.Abstractions.Interfaces;

namespace Diagnostics.Abstractions;

/// <summary>
/// Default <see cref="ICorrelationContext"/> implementation. Ambient state travels with the logical call flow via <see cref="AsyncLocal{T}"/>, so it survives awaits but not across disconnected threads/fire-and-forget work — same tradeoff as <c>HttpContext</c>.
/// The <see cref="AsyncLocal{T}"/> is an INSTANCE field, not static: in production there is exactly one instance app-wide (registered as a singleton via <c>AddDiagnostics</c>), so this behaves identically to a shared/static slot
/// — but it also means two separate instances (e.g. two unit tests each constructing their own <see cref="CorrelationContext"/>) get fully isolated ambient state instead of accidentally sharing one global slot process-wide.
/// </summary>
public sealed class CorrelationContext : ICorrelationContext
{
    private sealed record State(Guid CorrelationId, Guid? TransactionId, Guid? ParentTransactionId, string Category, string? User);

    private readonly AsyncLocal<State?> _ambient = new();

    public Guid CorrelationId
    {
        get
        {
            var current = _ambient.Value;
            if (current is not null)
            {
                return current.CorrelationId;
            }

            var generated = Guid.NewGuid();
            _ambient.Value = new State(generated, null, null, CategoryNames.None, null);
            return generated;
        }
    }

    public Guid? CurrentTransactionId => _ambient.Value?.TransactionId;

    public Guid? CurrentParentTransactionId => _ambient.Value?.ParentTransactionId;

    public string CurrentCategory => _ambient.Value?.Category ?? CategoryNames.None;

    public string? CurrentUser => _ambient.Value?.User;

    public void SetCorrelationId(Guid correlationId)
    {
        var current = _ambient.Value;
        _ambient.Value = current is null
            ? new State(correlationId, null, null, CategoryNames.None, null)
            : current with { CorrelationId = correlationId };
    }

    public void SetUser(string? user)
    {
        var current = _ambient.Value;
        _ambient.Value = current is null
            ? new State(Guid.NewGuid(), null, null, CategoryNames.None, user)
            : current with { User = user };
    }

    public IDisposable PushTransaction(Guid transactionId, Guid? parentTransactionId, string category)
    {
        var previous = _ambient.Value;
        var correlationId = previous?.CorrelationId ?? Guid.NewGuid();

        _ambient.Value = new State(correlationId, transactionId, parentTransactionId, category, previous?.User);

        return new RestoreScope(_ambient, previous);
    }

    private sealed class RestoreScope(AsyncLocal<State?> ambient, State? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ambient.Value = previous;
        }
    }
}
