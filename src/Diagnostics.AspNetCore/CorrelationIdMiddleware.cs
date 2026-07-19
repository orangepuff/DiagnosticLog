using Diagnostics.Abstractions.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Diagnostics.AspNetCore;

/// <summary>
/// Reads the inbound <c>X-Correlation-ID</c> header (generating one if absent or not a valid guid — the raw incoming value is preserved on <see cref="HttpContext.Items"/> so <see cref="TransactionMiddleware"/> can stash it in <c>sCustomAttributes</c>) and always echoes the resolved id back on the response. Design doc §9.3.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    internal const string RawIncomingItemKey = "Diagnostics.RawIncomingCorrelationId";

    public async Task InvokeAsync(HttpContext context, ICorrelationContext correlationContext)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();

        Guid correlationId;
        if (!string.IsNullOrEmpty(incoming) && Guid.TryParse(incoming, out var parsed))
        {
            correlationId = parsed;
        }
        else
        {
            correlationId = Guid.NewGuid();
            if (!string.IsNullOrEmpty(incoming))
            {
                context.Items[RawIncomingItemKey] = incoming;
            } 
        }

        correlationContext.SetCorrelationId(correlationId);
        context.Response.Headers[HeaderName] = correlationId.ToString();

        await next(context).ConfigureAwait(false);
    }
}
