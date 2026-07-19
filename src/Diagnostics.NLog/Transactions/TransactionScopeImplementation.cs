using System.Diagnostics;
using System.Text.Json;
using Diagnostics.Abstractions;
using Diagnostics.Abstractions.Interfaces;
using Diagnostics.NLog.Targets;

namespace Diagnostics.NLog.Transactions;

/// <summary>
/// Default <see cref="ITransactionScope"/>. Opens the ambient transaction/category for its lifetime and, on <see cref="Dispose"/>, computes duration and hands the completed <see cref="TransactionRecord"/> to <see cref="TransactionsTarget"/> — never blocking the caller (the target's own bounded/batched writer owns the actual DB write).
/// </summary>
internal sealed class TransactionScopeImplementation : ITransactionScope
{
    private readonly ICorrelationContext _correlationContext;
    private readonly TransactionsTarget _sink;
    private readonly IDisposable _ambientScope;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly TransactionRecord _record;
    private Dictionary<string, object?>? _customAttributes;
    private bool _disposed;

    public TransactionScopeImplementation(
        ICorrelationContext correlationContext,
        TransactionsTarget sink,
        string category,
        string? message)
    {
        _correlationContext = correlationContext;
        _sink = sink;

        Id = Guid.NewGuid();
        ParentId = correlationContext.CurrentTransactionId;
        _ambientScope = correlationContext.PushTransaction(Id, ParentId, category);

        _record = new TransactionRecord
        {
            Id = Id,
            ParentId = ParentId,
            CorrelationId = correlationContext.CorrelationId,
            Category = category,
            Message = message,
            StartTime = DateTime.UtcNow,
        };
    }

    public Guid Id { get; }

    public Guid? ParentId { get; }

    public void SetUser(string? user)
    {
        _record.User = user;
        _correlationContext.SetUser(user);
    }

    public void SetUrl(string? url, string? baseUrl = null)
    {
        _record.Url = url;
        _record.BaseUrl = baseUrl;
    }

    public void SetMessage(string? message) => _record.Message = message;

    public void SetCustomAttribute(string key, object? value)
    {
        _customAttributes ??= new Dictionary<string, object?>();
        _customAttributes[key] = value;
    }

    public void SetSql(string sql) => _record.Sql = sql;

    public void RequestJson(string json) => _record.RequestJson = json;

    public void RequestXml(string xml) => _record.RequestXml = xml;

    public void RequestText(string text) => _record.RequestText = text;

    public void ResponseJson(string json) => _record.ResponseJson = json;

    public void ResponseXml(string xml) => _record.ResponseXml = xml;

    public void ResponseText(string text) => _record.ResponseText = text;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopwatch.Stop();
        _record.DurationMs = (int)_stopwatch.ElapsedMilliseconds;

        if (_customAttributes is { Count: > 0 })
        {
            _record.CustomAttributesJson = JsonSerializer.Serialize(_customAttributes);
        }

        try
        {
            _sink.Enqueue(_record);
        }
        finally
        {
            _ambientScope.Dispose();
        }
    }
}
