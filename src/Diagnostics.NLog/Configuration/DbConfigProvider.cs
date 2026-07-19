using Dapper;
using Diagnostics.NLog.Interfaces;
using Microsoft.Data.SqlClient;

namespace Diagnostics.NLog.Configuration;

/// <summary>
/// Result of resolving this app's logging config for the current environment.
/// </summary>
public sealed record ConfigSnapshot(IReadOnlyList<ParsedRule> Rules, DateTime? UpdatedTime);

/// <summary>
/// Loads NLog rules for <c>LoggerName</c>/<c>EnvironmentName</c> from <c>[dbo].[Configurations]</c> (Dapper — small, cached reads per design doc §4).
/// Falls back to a permissive default (min level Info, "*") when nothing is configured yet, so a fresh environment still logs instead of going silent.
/// </summary>
public sealed class DbConfigProvider(string connectionString, IEnvironmentResolver resolver)
{
    private static readonly IReadOnlyList<ParsedRule> DefaultRules = [new ParsedRule("*", "Info")];

    public async Task<ConfigSnapshot> LoadAsync(string loggerName, string environmentName, CancellationToken ct = default)
    {
        int environmentId;
        try
        {
            environmentId = await resolver.ResolveIdAsync(environmentName, null, null, ct).ConfigureAwait(false);
        }
        catch
        {
            // DiagnosticLogs unreachable — never block app startup on it (§5/§7).
            return new ConfigSnapshot(DefaultRules, null);
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);

            var rows = (await connection.QueryAsync<ConfigurationRow>(
                """
                SELECT iId AS IId, sLoggerName AS SLoggerName, iEnvironmentId AS IEnvironmentId,
                       CAST(xValue AS NVARCHAR(MAX)) AS XmlValue, dtUpdatedTime AS DtUpdatedTime
                FROM dbo.Configurations
                WHERE sLoggerName = @loggerName
                  AND (iEnvironmentId = @environmentId OR iEnvironmentId IS NULL)
                """,
                new { loggerName, environmentId }).ConfigureAwait(false))
                .ToList();

            // Environment-specific row wins over the global (NULL) default when both exist.
            var row = rows
                .OrderByDescending(r => r.EnvironmentId.HasValue)
                .FirstOrDefault();

            if (row is null)
            {
                return new ConfigSnapshot(DefaultRules, null);
            }
            
            var rules = NLogRuleXmlParser.ParseRules(row.XmlValue);
            return new ConfigSnapshot(rules.Count > 0 
                ? rules 
                : DefaultRules, row.UpdatedTime);
        }
        catch
        {
            return new ConfigSnapshot(DefaultRules, null);
        }
    }
}
