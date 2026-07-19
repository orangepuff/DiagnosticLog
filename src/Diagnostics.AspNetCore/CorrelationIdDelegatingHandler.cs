using Diagnostics.Abstractions.Interfaces;

namespace Diagnostics.AspNetCore;

/// <summary>
/// Opt-in outbound propagation: forwards the current <see cref="ICorrelationContext.CorrelationId"/> as <c>X-Correlation-ID</c> on outgoing <see cref="HttpClient"/> calls (design doc §9.3).
/// Register via <c>services.AddHttpClient(...).AddDiagnosticsCorrelationPropagation()</c>.
/// </summary>
public sealed class CorrelationIdDelegatingHandler(ICorrelationContext correlationContext) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(CorrelationIdMiddleware.HeaderName);
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, correlationContext.CorrelationId.ToString());
        return base.SendAsync(request, cancellationToken);
    }
}
