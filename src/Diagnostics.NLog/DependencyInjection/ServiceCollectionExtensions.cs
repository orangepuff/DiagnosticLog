using Diagnostics.Abstractions;
using Diagnostics.Abstractions.Interfaces;
using Diagnostics.NLog.Configuration;
using Diagnostics.NLog.Interfaces;
using Diagnostics.NLog.Lookups.Resolvers;
using Diagnostics.NLog.Targets;
using Diagnostics.NLog.Transactions;
using Microsoft.Extensions.DependencyInjection;
using NLog.Extensions.Logging;

namespace Diagnostics.NLog.DependencyInjection;

/// <summary>
/// Composition-root entry point for the Diagnostics library (mirrors <c>AddPdfEngine</c>).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Diagnostics logging pipeline: <see cref="ICorrelationContext"/>, <see cref="ITransactionLogger"/>, the DB-backed config poller, and the NLog↔ILogger bridge.
    /// Call once at the composition root; for web apps, also add the <c>Diagnostics.AspNetCore</c> middleware.
    /// </summary>
    public static IServiceCollection AddDiagnostics(this IServiceCollection services, Action<DiagnosticsOptions> configure)
    {
        var options = new DiagnosticsOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException("DiagnosticsOptions.ConnectionString must be set.");
        }

        if (string.IsNullOrWhiteSpace(options.LoggerName))
        {
            throw new InvalidOperationException("DiagnosticsOptions.LoggerName must be set.");
        }
        
        services.AddSingleton(options);
        services.AddSingleton<ICorrelationContext, CorrelationContext>();
        services.AddSingleton<IEnvironmentResolver>(_ => new EnvironmentResolver(options.ConnectionString));
        services.AddSingleton<ICategoryResolver>(_ => new CategoryResolver(options.ConnectionString));
        services.AddSingleton(sp => new DbConfigProvider(options.ConnectionString, sp.GetRequiredService<IEnvironmentResolver>()));

        services.AddSingleton(sp => new LogsTarget(
            options.ConnectionString,
            sp.GetRequiredService<IEnvironmentResolver>(),
            sp.GetRequiredService<ICategoryResolver>(),
            options));

        services.AddSingleton(sp => new TransactionsTarget(
            options.ConnectionString,
            sp.GetRequiredService<IEnvironmentResolver>(),
            sp.GetRequiredService<ICategoryResolver>(),
            options));

        services.AddSingleton<TransactionLoggerImplementation>();
        services.AddSingleton<ITransactionLogger>(sp => sp.GetRequiredService<TransactionLoggerImplementation>());

        services.AddHostedService<DiagnosticsConfigHostedService>();

        // App code depends only on ILogger; NLog owns write timing (§3). Coexists with any other logging providers already registered (Console, etc.) — this just adds NLog on top.
        services.AddLogging(builder => builder.AddNLog());

        return services;
    }
}
