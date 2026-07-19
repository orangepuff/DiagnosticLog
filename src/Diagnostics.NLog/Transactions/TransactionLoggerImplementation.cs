using Diagnostics.Abstractions.Interfaces;
using Diagnostics.NLog.Targets;

namespace Diagnostics.NLog.Transactions;

/// <inheritdoc cref="ITransactionLogger"/>
public sealed class TransactionLoggerImplementation(ICorrelationContext correlationContext, TransactionsTarget sink) : ITransactionLogger
{
    public ITransactionScope BeginTransaction(string category, string? message = null) => new TransactionScopeImplementation(correlationContext, sink, category, message);
}
