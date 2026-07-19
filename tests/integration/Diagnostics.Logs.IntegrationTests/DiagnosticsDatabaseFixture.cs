using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Diagnostics.Abstractions.Interfaces;
using Diagnostics.NLog.DependencyInjection;
using Diagnostics.NLog.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Diagnostics.Logs.IntegrationTests;

/// <summary>
/// Starts the real Diagnostics pipeline against a real SQL Server for the whole test class.
/// Ensures the DiagnosticLogs schema exists by running docs/diagnostics-logs-schema.sql, which is idempotent and safe to re-run.
/// Connection string and logger/environment name come from appsettings.json (ConnectionStrings:DiagnosticLogs, Diagnostics:*) — the same convention OCRWeb.API's Program.cs uses.
/// Requires a reachable SQL Server — see CLAUDE.md's SQL Server setup section.
/// </summary>
public sealed class DiagnosticsDatabaseFixture : IAsyncLifetime
{
    private readonly string _masterConnectionString;
    private readonly string _databaseName;
    private readonly string _loggerName;
    private readonly string _environmentName;

    private IHost? _host;

    public DiagnosticsDatabaseFixture()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .Build();

        ConnectionString = configuration.GetConnectionString("DiagnosticLogs")
            ?? throw new InvalidOperationException("ConnectionStrings:DiagnosticLogs is not configured in appsettings.json.");

        _loggerName = configuration["Diagnostics:LoggerName"] ?? "NLogLogger";
        _environmentName = configuration["Diagnostics:EnvironmentName"] ?? "DEV";

        var appBuilder = new SqlConnectionStringBuilder(ConnectionString);
        _databaseName = appBuilder.InitialCatalog;

        var masterBuilder = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = string.Empty };
        _masterConnectionString = masterBuilder.ConnectionString;
    }

    public string ConnectionString { get; }

    public ICorrelationContext CorrelationContext => _host!.Services.GetRequiredService<ICorrelationContext>();

    public ILogger Logger => _host!.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Diagnostics.Logs.IntegrationTests");

    public IEnvironmentResolver EnvironmentResolver => _host!.Services.GetRequiredService<IEnvironmentResolver>();

    public ICategoryResolver CategoryResolver => _host!.Services.GetRequiredService<ICategoryResolver>();

    public async Task InitializeAsync()
    {
        await EnsureSchemaAsync();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddDiagnostics(options =>
        {
            options.ConnectionString = ConnectionString;
            options.LoggerName = _loggerName;
            options.EnvironmentName = _environmentName;
            options.FlushBatchSize = 1;
            options.FlushInterval = TimeSpan.FromMilliseconds(200);
            options.ConfigPollInterval = TimeSpan.FromSeconds(2);
        });

        _host = hostBuilder.Build();
        await _host.StartAsync();

        // DiagnosticsConfigHostedService applies the real NLog rule config on a background task
        // after StartAsync returns, so the very first log call(s) can still race ahead of it and
        // get silently dropped. Callers must retry the log call itself, not just poll the DB —
        // see LogsIntegrationTests.LogUntilRowAppearsAsync.
    }

    public async Task DisposeAsync()
    {
        if (_host is null)
        {
            return;
        }

        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task EnsureSchemaAsync()
    {
        var script = await File.ReadAllTextAsync(SchemaFilePath());

        // The reference script hardcodes the "DiagnosticLogs" database name (CREATE DATABASE /
        // USE) — retarget it to whatever database the connection string actually configures, so a
        // renamed test database (e.g. DiagnosticLogs_IntegrationTest) gets its own schema instead
        // of silently applying it to a same-named but different database.
        script = Regex.Replace(script, @"\bDiagnosticLogs\b", _databaseName);

        await using var connection = new SqlConnection(_masterConnectionString);
        await connection.OpenAsync();

        foreach (var batch in SplitBatches(script))
        {
            await using var command = new SqlCommand(batch, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static IEnumerable<string> SplitBatches(string script)
    {
        // GO is a batch separator recognized by sqlcmd/SSMS, not real T-SQL — split on it manually.
        return Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Select(batch => batch.Trim())
            .Where(batch => batch.Length > 0);
    }

    private static string SchemaFilePath([CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)!;
        return Path.GetFullPath(Path.Combine(sourceDirectory, "..", "..", "..", "..", "..", "docs", "diagnostics-logs-schema.sql"));
    }
}
