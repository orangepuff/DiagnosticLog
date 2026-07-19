namespace Diagnostics.NLog.Targets;

/// <summary>
/// Row shape for <c>[dbo].[Logs]</c> (identity <c>iId</c> is DB-generated, omitted here).
/// </summary>
public sealed record LogRow(
    Guid? TransactionId,
    int EnvironmentId,
    int CategoryId,
    Guid CorrelationId,
    DateTime TimeLogged,
    string? Message,
    string? Exception,
    string Severity,
    string? User,
    string? CustomAttributesJson);
