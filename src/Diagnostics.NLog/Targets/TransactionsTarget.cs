using System.Data;
using Diagnostics.Abstractions;
using Diagnostics.Abstractions.Interfaces;
using Diagnostics.NLog.Buffering;
using Diagnostics.NLog.Interfaces;
using Microsoft.Data.SqlClient;

namespace Diagnostics.NLog.Targets;

/// <summary>
/// Sink for completed <see cref="ITransactionScope"/> spans: bounded/batched → <c>SqlBulkCopy</c> → <c>[dbo].[Transactions]</c> (design doc §3/§6).
/// Named "Target" to mirror <see cref="LogsTarget"/>, but fed directly by <c>TransactionScopeImpl.Dispose</c> (<see cref="Enqueue"/>) rather than through NLog's logger dispatch — a transaction always flushes exactly one row regardless of NLog min level rules, matching the unconditional "measures duration and flushes on dispose" semantics in §3.
/// </summary>
public sealed class TransactionsTarget : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly IEnvironmentResolver _environmentResolver;
    private readonly ICategoryResolver _categoryResolver;
    private readonly DiagnosticsOptions _options;
    private readonly WriteMetrics _metrics = new();
    private readonly Dictionary<string, int> _categoryIdCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _categoryLock = new();

    private BoundedBatchWriter<TransactionRecord>? _writer;
    private int _environmentId;

    public TransactionsTarget(
        string connectionString,
        IEnvironmentResolver environmentResolver,
        ICategoryResolver categoryResolver,
        DiagnosticsOptions options)
    {
        _connectionString = connectionString;
        _environmentResolver = environmentResolver;
        _categoryResolver = categoryResolver;
        _options = options;
    }

    public WriteMetrics Metrics => _metrics;

    /// <summary>
    /// Idempotent — safe to call repeatedly (e.g. on every DB-config poll).
    /// </summary>
    public void EnsureStarted()
    {
        if (_writer is not null)
            return;

        try
        {
            _environmentId = _environmentResolver
                .ResolveIdAsync(_options.EnvironmentName, _options.EnvironmentVersion, _options.EnvironmentUrl)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // DiagnosticLogs unreachable at startup — never let this take the host down (§7).
            // Same fallback reasoning as LogsTarget.InitializeTarget.
        }

        _writer = new BoundedBatchWriter<TransactionRecord>(
            _options.MaxQueueSize,
            _options.FlushBatchSize,
            _options.FlushInterval,
            FlushBatchAsync,
            WriteFallback,
            _metrics);
    }

    public void Enqueue(TransactionRecord record)
    {
        EnsureStarted();
        _writer?.Enqueue(record);
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

        var resolved = _categoryResolver.ResolveIdAsync(category).GetAwaiter().GetResult();

        lock (_categoryLock)
        {
            _categoryIdCache[category] = resolved;
        }

        return resolved;
    }

    private async Task FlushBatchAsync(IReadOnlyList<TransactionRecord> rows, CancellationToken ct)
    {
        using var table = new DataTable();
        table.Columns.Add("sId", typeof(Guid));
        table.Columns.Add("sParentId", typeof(Guid));
        table.Columns.Add("iEnvironmentId", typeof(int));
        table.Columns.Add("iCategoryId", typeof(int));
        table.Columns.Add("sCorrelationId", typeof(Guid));
        table.Columns.Add("sMessage", typeof(string));
        table.Columns.Add("sUrl", typeof(string));
        table.Columns.Add("dtStartTime", typeof(DateTime));
        table.Columns.Add("iDuration", typeof(int));
        table.Columns.Add("xRequestXml", typeof(string));
        table.Columns.Add("sRequestJson", typeof(string));
        table.Columns.Add("sRequestText", typeof(string));
        table.Columns.Add("xResponseXml", typeof(string));
        table.Columns.Add("sResponseJson", typeof(string));
        table.Columns.Add("sResponseText", typeof(string));
        table.Columns.Add("sUser", typeof(string));
        table.Columns.Add("sCustomAttributes", typeof(string));
        table.Columns.Add("sSql", typeof(string));
        table.Columns.Add("sBaseUrl", typeof(string));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.Id,
                (object?)row.ParentId ?? DBNull.Value,
                _environmentId,
                ResolveCategoryIdCached(row.Category),
                row.CorrelationId,
                (object?)row.Message ?? DBNull.Value,
                (object?)row.Url ?? DBNull.Value,
                row.StartTime,
                (object?)row.DurationMs ?? DBNull.Value,
                (object?)row.RequestXml ?? DBNull.Value,
                (object?)row.RequestJson ?? DBNull.Value,
                (object?)row.RequestText ?? DBNull.Value,
                (object?)row.ResponseXml ?? DBNull.Value,
                (object?)row.ResponseJson ?? DBNull.Value,
                (object?)row.ResponseText ?? DBNull.Value,
                (object?)row.User ?? DBNull.Value,
                (object?)row.CustomAttributesJson ?? DBNull.Value,
                (object?)row.Sql ?? DBNull.Value,
                (object?)row.BaseUrl ?? DBNull.Value);
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        using var bulkCopy = new SqlBulkCopy(connection);
        bulkCopy.DestinationTableName = "dbo.Transactions";

        foreach (DataColumn column in table.Columns)
        {
            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulkCopy.WriteToServerAsync(table, ct).ConfigureAwait(false);
    }

    private void WriteFallback(IReadOnlyList<TransactionRecord> rows)
    {
        var path = ResolveFallbackPath(_options.LocalFallbackLogFile);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllLines(path, rows.Select(r =>  $"[TRANSACTION] {r.StartTime:O} id={r.Id} parent={r.ParentId} category={r.Category} " + $"duration={r.DurationMs}ms message={r.Message}"));
    }

    private static string ResolveFallbackPath(string template) => template.Replace("${shortdate}", DateTime.UtcNow.ToString("yyyy-MM-dd"));

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        } 
    }
}
