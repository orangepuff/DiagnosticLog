namespace Diagnostics.Abstractions.Interfaces;

/// <summary>
/// One open operation span. Measures duration and flushes a single row to <c>[dbo].[Transactions]</c> on <see cref="IDisposable.Dispose"/> (via the adapter's NLog target, so the actual DB write is async/batched — the caller never blocks on it).
/// Body capture is explicit and per-scope: only the <c>Request*</c>/<c>Response*</c> methods that are actually called end up persisted — see design doc §3.
/// </summary>
public interface ITransactionScope : IDisposable
{
    /// <summary>
    /// Id of this scope (also <c>[Transactions].sId</c>).
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// Id of the enclosing scope, if this one was opened while another was active.
    /// </summary>
    Guid? ParentId { get; }

    /// <summary>
    /// Sets the current user for this scope (maps to <c>sUser</c>).
    /// </summary>
    void SetUser(string? user);

    /// <summary>
    /// Sets the request url/base url for this scope.
    /// </summary>
    void SetUrl(string? url, string? baseUrl = null);

    /// <summary>
    /// Overrides the operation summary set when the scope was opened.
    /// </summary>
    void SetMessage(string? message);

    /// <summary>
    /// Adds a key to the structured <c>sCustomAttributes</c> JSON blob for this scope.
    /// </summary>
    void SetCustomAttribute(string key, object? value);

    /// <summary>
    /// Captures the SQL executed during this scope (maps to <c>sSql</c>), if enabled.
    /// </summary>
    void SetSql(string sql);

    /// <summary>
    /// Persists <paramref name="json"/> as the request body (maps to <c>sRequestJson</c>).
    /// </summary>
    void RequestJson(string json);

    /// <summary>
    /// Persists <paramref name="xml"/> as the request body (maps to <c>xRequestXml</c>).
    /// </summary>
    void RequestXml(string xml);

    /// <summary>
    /// Persists <paramref name="text"/> as the request body (maps to <c>sRequestText</c>).
    /// </summary>
    void RequestText(string text);

    /// <summary>
    /// Persists <paramref name="json"/> as the response body (maps to <c>sResponseJson</c>).
    /// </summary>
    void ResponseJson(string json);

    /// <summary>
    /// Persists <paramref name="xml"/> as the response body (maps to <c>xResponseXml</c>).
    /// </summary>
    void ResponseXml(string xml);

    /// <summary>
    /// Persists <paramref name="text"/> as the response body (maps to <c>sResponseText</c>).
    /// </summary>
    void ResponseText(string text);
}
