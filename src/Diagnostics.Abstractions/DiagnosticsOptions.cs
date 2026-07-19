namespace Diagnostics.Abstractions;

/// <summary>
/// Infrastructure wiring for the Diagnostics library, set in code at the composition root (see design doc §5 — this is deliberately separate from the levels/rules/filters that live in the DB-stored NLog XML, which ops can retune without a re-deployment).
/// </summary>
public sealed class DiagnosticsOptions
{
    public const string SectionName = "Diagnostics";

    /// <summary>
    /// Connection string to the shared <c>DiagnosticLogs</c> database.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Key into <c>[dbo].[Configurations].sLoggerName</c> identifying this app/component (e.g. "NLogLogger").
    /// </summary>
    public string LoggerName { get; set; } = string.Empty;

    /// <summary>
    /// Name resolved/inserted into <c>[dbo].[Environments].sName</c> (e.g. "DEV").
    /// </summary>
    public string EnvironmentName { get; set; } = "DEV";

    /// <summary>Optional app version recorded on the <c>Environments</c> row.</summary>
    public string? EnvironmentVersion { get; set; }

    /// <summary>
    /// Optional app base url recorded on the <c>Environments</c> row.
    /// </summary>
    public string? EnvironmentUrl { get; set; }

    /// <summary>
    /// Flush the write buffer after this many rows (balanced default: 500 — §9.6).
    /// </summary>
    public int FlushBatchSize { get; set; } = 500;

    /// <summary>
    /// Flush the write buffer after this much time, whichever comes first (default 2s).
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Bounded in-memory queue size per sink; overflow drops + increments a metric.
    /// </summary>
    public int MaxQueueSize { get; set; } = 10_000;

    /// <summary>
    /// How often to poll <c>Configurations.dtUpdatedTime</c> for hot-reload (§5).
    /// </summary>
    public TimeSpan ConfigPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Local file target used as a fallback sink when <c>DiagnosticLogs</c> is unreachable —
    /// logging must never take the app down (§7). Supports NLog layout renderers (e.g. ${shortdate}).
    /// </summary>
    public string LocalFallbackLogFile { get; set; } = "logs/diagnostics-fallback-${shortdate}.log";

    /// <summary>
    /// Max size, in bytes, of any single captured request/response body before truncation.
    /// </summary>
    public int MaxBodyCaptureSizeBytes { get; set; } = 32 * 1024;

    /// <summary>
    /// Optional redaction hook applied to captured bodies before they are persisted/queued (e.g. strip secrets/PII). Identity by default.
    /// </summary>
    public Func<string, string> Redact { get; set; } = static body => body;
}
