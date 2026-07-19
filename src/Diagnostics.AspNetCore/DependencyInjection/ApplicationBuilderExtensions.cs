using Microsoft.AspNetCore.Builder;

namespace Diagnostics.AspNetCore.DependencyInjection;

public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the correlation-id and per-request transaction middleware, in the required order (correlation must resolve before the transaction scope opens).
    /// Call after <c>AddDiagnostics</c> has been called on the service collection.
    /// </summary>
    public static IApplicationBuilder UseDiagnostics(this IApplicationBuilder app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<TransactionMiddleware>();
        return app;
    }
} 
