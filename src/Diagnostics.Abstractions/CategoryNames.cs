using Diagnostics.Abstractions.Interfaces;

namespace Diagnostics.Abstractions;

public static class CategoryNames
{
    /// <summary>
    /// Guardrail value used to satisfy the NOT NULL category column when a line log is written outside any <see cref="ITransactionLogger.BeginTransaction"/> scope.
    /// Surfaced as a metric so un-categorized logs get noticed rather than silently defaulted.
    /// </summary>
    public const string None = "(none)";
}
