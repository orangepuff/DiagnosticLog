using Microsoft.Extensions.DependencyInjection;

namespace Diagnostics.AspNetCore.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ASP.NET Core integration pieces (the outbound propagation handler).
    /// </summary>
    public static IServiceCollection AddDiagnosticsAspNetCore(this IServiceCollection services)
    {
        services.AddTransient<CorrelationIdDelegatingHandler>();
        return services;
    }

    /// <summary>
    /// Opts an <see cref="IHttpClientBuilder"/> into outbound X-Correlation-ID propagation.
    /// </summary>
    public static IHttpClientBuilder AddDiagnosticsCorrelationPropagation(this IHttpClientBuilder builder) => builder.AddHttpMessageHandler<CorrelationIdDelegatingHandler>();
}
