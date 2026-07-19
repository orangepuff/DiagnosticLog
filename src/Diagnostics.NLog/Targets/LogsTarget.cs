using System.Data;
using System.Text.Json;
using Diagnostics.Abstractions;
using Diagnostics.Abstractions.Interfaces;
using Diagnostics.NLog.Buffering;
using Diagnostics.NLog.Interfaces;
using Microsoft.Data.SqlClient;
using NLog;
using NLog.Targets;

namespace Diagnostics.NLog.Targets;

/// <summary>
/// Custom NLog target: one row per <c>ILogger</c> event → bounded/batched → <c>SqlBulkCopy</c> → <c>[dbo].[Logs]</c> (design doc §3/§6).
/// <c>iCategoryId</c> is resolved from the ambient transaction's category (or the <see cref="CategoryNames.None"/> guardrail) — never from the NLog logger name, which is instead captured into <c>sCustomAttributes</c>.
/// </summary>
[Target("DiagnosticsLogs")]
public sealed class LogsTarget : Target
{
    private readonly string _connectionString;
    private readonly IEnvironmentResolver _environmentResolver;
    private readonly ICategoryResolver _categoryResolver;
    private readonly DiagnosticsOptions _options;
    private readonly WriteMetrics _metrics = new();
    private readonly Dictionary<string, int> _categoryIdCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _categoryLock = new();

    // Populated by AmbientContextCapturingTargetWrapper, not read live here — see its remarks.
    // internal (not private): shared with that wrapper class, namespaced so they can't collide
    // with structured-logging properties an application passes into ILogger calls.
    internal const string CorrelationIdPropertyKey = "__Diagnostics.CorrelationId";
    internal const string TransactionIdPropertyKey = "__Diagnostics.TransactionId";
    internal const string CategoryPropertyKey = "__Diagnostics.Category";
    internal const string UserPropertyKey = "__Diagnostics.User";

    private BoundedBatchWriter<LogRow>? _writer;
    private int _environmentId;

    public LogsTarget(
        string connectionString,
        IEnvironmentResolver environmentResolver,
        ICategoryResolver categoryResolver,
        DiagnosticsOptions options)
    {
        _connectionString = connectionString;
        _environmentResolver = environmentResolver;
        _categoryResolver = categoryResolver;
        _options = options;
        Name = "DiagnosticsLogs";
    }

    /// <summary>
    /// Cumulative enqueue/drop/flush counters for this sink.
    /// </summary>
    public WriteMetrics Metrics => _metrics;

    protected override void InitializeTarget()
    {
        base.InitializeTarget();

        // Config is reapplied on every DB-config poll (see DiagnosticsConfigHostedService), which hands this SAME singleton instance to a new LoggingConfiguration.
        // Guard against restarting the batch pipeline (and losing whatever is mid-flight) on every reapply.
        if (_writer is not null)
        {
            return;
        }

        try
        {
            _environmentId = _environmentResolver
                .ResolveIdAsync(_options.EnvironmentName, _options.EnvironmentVersion, _options.EnvironmentUrl)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // DiagnosticLogs unreachable at startup — never let this take the host down (§7).
            // Leave _environmentId at its default (0); every write attempt below will still go through the normal flush-retry-fallback path and land in the local fallback file until the DB comes back and a later config poll resolves the real id.
        }

        _writer = new BoundedBatchWriter<LogRow>(
            _options.MaxQueueSize,
            _options.FlushBatchSize,
            _options.FlushInterval,
            FlushBatchAsync,
            WriteFallback,
            _metrics);
    }

    /// <summary>
    /// Reads the ambient correlation/transaction/category snapshot from <c>logEvent.Properties</c>.
    /// This target is wrapped in <c>AsyncTargetWrapper</c>, so this method runs later, on NLog's own internal drain thread, where the caller's <c>AsyncLocal</c>-backed ambient state no longer exists.
    /// <see cref="AmbientContextCapturingTargetWrapper"/> snapshots that state into the properties synchronously, on the caller's own thread, before the event is queued — never read <see cref="ICorrelationContext"/> directly in this method.
    /// </summary>
    protected override void Write(LogEventInfo logEvent)
    {
        var category = logEvent.Properties.TryGetValue(CategoryPropertyKey, out var categoryValue) && categoryValue is string categoryName
            ? categoryName
            : CategoryNames.None;
        var categoryId = ResolveCategoryIdCached(category);

        var transactionId = logEvent.Properties.TryGetValue(TransactionIdPropertyKey, out var transactionValue)
            ? transactionValue as Guid?
            : null;

        var correlationId = logEvent.Properties.TryGetValue(CorrelationIdPropertyKey, out var correlationValue) && correlationValue is Guid correlationGuid
            ? correlationGuid
            : Guid.NewGuid();

        var row = new LogRow(
            TransactionId: transactionId,
            EnvironmentId: _environmentId,
            CategoryId: categoryId,
            CorrelationId: correlationId,
            TimeLogged: logEvent.TimeStamp.ToUniversalTime(),
            Message: logEvent.FormattedMessage,
            Exception: logEvent.Exception?.ToString(),
            Severity: logEvent.Level.Name,
            User: logEvent.Properties.TryGetValue(UserPropertyKey, out var user) ? user as string : null,
            CustomAttributesJson: SerializeCustomAttributes(logEvent));

        _writer?.Enqueue(row);
    }

    private int ResolveCategoryIdCached(string category)
    {
        lock (_categoryLock)
        {
            if (_categoryIdCache.TryGetValue(category, out var cached))
            {
                return cached;
            } 
        }

        // Rare path (new category name) — resolver itself caches, so subsequent calls are fast.
        var resolved = _categoryResolver.ResolveIdAsync(category).GetAwaiter().GetResult();

        lock (_categoryLock)
        {
            _categoryIdCache[category] = resolved;
        }

        return resolved;
    }

    private static string SerializeCustomAttributes(LogEventInfo logEvent)
    {
        var attributes = new Dictionary<string, object?> { ["LoggerName"] = logEvent.LoggerName };

        foreach (var property in logEvent.Properties)
        {
            var key = property.Key?.ToString();
            if (string.IsNullOrEmpty(key) || key == "User" || IsInternalPropertyKey(key))
            {
                continue;
            }

            attributes[key] = property.Value;
        }

        return JsonSerializer.Serialize(attributes);
    }

    private static bool IsInternalPropertyKey(string key) =>
        key is CorrelationIdPropertyKey or TransactionIdPropertyKey or CategoryPropertyKey or UserPropertyKey;

    private async Task FlushBatchAsync(IReadOnlyList<LogRow> rows, CancellationToken ct)
    {
        using var table = new DataTable();
        table.Columns.Add("sTransactionId", typeof(Guid));
        table.Columns.Add("iEnvironmentId", typeof(int));
        table.Columns.Add("iCategoryId", typeof(int));
        table.Columns.Add("sCorrelationId", typeof(Guid));
        table.Columns.Add("dtTimeLogged", typeof(DateTime));
        table.Columns.Add("sMessage", typeof(string));
        table.Columns.Add("sException", typeof(string));
        table.Columns.Add("sSeverity", typeof(string));
        table.Columns.Add("sUser", typeof(string));
        table.Columns.Add("sCustomAttributes", typeof(string));

        foreach (var row in rows)
        {
            table.Rows.Add(
                (object?)row.TransactionId ?? DBNull.Value,
                row.EnvironmentId,
                row.CategoryId,
                row.CorrelationId,
                row.TimeLogged,
                (object?)row.Message ?? DBNull.Value,
                (object?)row.Exception ?? DBNull.Value,
                row.Severity,
                (object?)row.User ?? DBNull.Value,
                (object?)row.CustomAttributesJson ?? DBNull.Value);
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        using var bulkCopy = new SqlBulkCopy(connection);
        bulkCopy.DestinationTableName = "dbo.Logs";
        foreach (DataColumn column in table.Columns)
        {
            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }
        
        await bulkCopy.WriteToServerAsync(table, ct).ConfigureAwait(false);
    }

    private void WriteFallback(IReadOnlyList<LogRow> rows)
    {
        var path = ResolveFallbackPath(_options.LocalFallbackLogFile);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        File.AppendAllLines(path, rows.Select(r => JsonSerializer.Serialize(r)));
    }

    private static string ResolveFallbackPath(string template) => template.Replace("${shortdate}", DateTime.UtcNow.ToString("yyyy-MM-dd"));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _writer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }
}
