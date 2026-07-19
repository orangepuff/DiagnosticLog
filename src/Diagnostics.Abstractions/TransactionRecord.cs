using Diagnostics.Abstractions.Interfaces;

namespace Diagnostics.Abstractions;

/// <summary>
/// Shape of one completed <see cref="ITransactionScope"/>. Handed off to the NLog target on <c>Dispose</c>.
/// Field names mirror <c>[dbo].[Transactions]</c> (see docs/diagnostics-logs-schema.sql). <see cref="Category"/> and environment are names, not resolved ids. <c>EnvironmentResolver</c>/<c>CategoryResolver</c> resolve ids at write time, keeping this DTO free of any DB dependency.
/// </summary>
public sealed class TransactionRecord
{
    public required Guid Id { get; init; }
    public Guid? ParentId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required string Category { get; init; }
    public string? Message { get; set; }
    public string? Url { get; set; }
    public string? BaseUrl { get; set; }
    public required DateTime StartTime { get; init; }
    public int? DurationMs { get; set; }
    public string? RequestXml { get; set; }
    public string? RequestJson { get; set; }
    public string? RequestText { get; set; }
    public string? ResponseXml { get; set; }
    public string? ResponseJson { get; set; }
    public string? ResponseText { get; set; }
    public string? User { get; set; }
    public string? CustomAttributesJson { get; set; }
    public string? Sql { get; set; }
}
