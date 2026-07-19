using Diagnostics.Abstractions.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Diagnostics.AspNetCore;

/// <summary>
/// Opens one root <see cref="ITransactionScope"/> per request, capturing url/base url/user/ duration/status/correlation — metadata only.
/// It deliberately never reads the request or response body: bodies are persisted only where application code opts in via <c>tx.RequestJson/Xml/Text</c> and <c>tx.ResponseJson/Xml/Text</c> (design doc §3).
/// Must run after <see cref="CorrelationIdMiddleware"/> — see <c>UseDiagnostics</c>.
/// </summary>
public sealed class TransactionMiddleware(RequestDelegate next)
{
    /// <summary>Functional area used for the request-level root span when nothing more specific applies.</summary>
    public const string DefaultCategory = "HttpRequest";

    public async Task InvokeAsync(HttpContext context, ITransactionLogger transactionLogger)
    {
        var request = context.Request;
        using var scope = transactionLogger.BeginTransaction(DefaultCategory, $"{request.Method} {request.Path}");

        scope.SetUrl(
            url: $"{request.Path}{request.QueryString}",
            baseUrl: $"{request.Scheme}://{request.Host}");

        if (context.User.Identity?.IsAuthenticated == true)
        {
            scope.SetUser(context.User.Identity.Name);
        }

        if (context.Items.TryGetValue(CorrelationIdMiddleware.RawIncomingItemKey, out var raw) &&
            raw is string rawValue)
        {
            scope.SetCustomAttribute("RawIncomingCorrelationId", rawValue);
        }

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            scope.SetCustomAttribute("StatusCode", context.Response.StatusCode);
        }
    }
}
