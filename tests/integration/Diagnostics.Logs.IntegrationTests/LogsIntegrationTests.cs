using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Diagnostics.Logs.IntegrationTests;

/// <summary>
/// Exercises the real Diagnostics pipeline end-to-end.
/// Verifies a log written through <see cref="ILogger"/> lands in the real DiagnosticLogs database with the expected column values.
/// Requires a reachable SQL Server — see <see cref="DiagnosticsDatabaseFixture"/>.
/// </summary>
[Collection(nameof(DiagnosticsDatabaseCollection))]
public sealed class LogsIntegrationTests(DiagnosticsDatabaseFixture fixture)
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task LogInformation_PersistsToLogsTable_WithExpectedColumns()
    {
        var correlationId = Guid.NewGuid();
        var marker = $"integration-test-{correlationId:N}";
        fixture.CorrelationContext.SetCorrelationId(correlationId);

        var row = await LogUntilRowAppearsAsync(correlationId, marker);

        Assert.NotNull(row);
        Assert.Contains(marker, row!.SMessage);
        Assert.Equal("Info", row.SSeverity);
        Assert.True(row.IEnvironmentId > 0, "Expected iEnvironmentId to resolve to a real row.");
        Assert.True(row.ICategoryId > 0, "Expected iCategoryId to resolve to a real row.");
        Assert.Contains("LoggerName", row.SCustomAttributes);
    }

    /// <summary>
    /// Re-issues the log call on every poll, not just once.
    /// NLog's rule config is still being applied asynchronously by DiagnosticsConfigHostedService when the host starts, so a single early log call can race ahead of it and get silently dropped — a later retry, once the real rule is in place, succeeds.
    /// </summary>
    private async Task<LogRowRecord?> LogUntilRowAppearsAsync(Guid correlationId, string marker)
    {
        var deadline = DateTime.UtcNow + PollTimeout;

        while (DateTime.UtcNow < deadline)
        {
            fixture.Logger.LogInformation("Diagnostics integration test marker {Marker}", marker);

            var row = await TryReadRowAsync(correlationId);
            if (row is not null)
            {
                return row;
            }

            await Task.Delay(PollInterval);
        }

        return null;
    }

    private async Task<LogRowRecord?> TryReadRowAsync(Guid correlationId)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);

        return await connection.QuerySingleOrDefaultAsync<LogRowRecord>(
            """
            SELECT sMessage AS SMessage, sSeverity AS SSeverity, iEnvironmentId AS IEnvironmentId,
                   iCategoryId AS ICategoryId, sCustomAttributes AS SCustomAttributes
            FROM dbo.Logs
            WHERE sCorrelationId = @correlationId
            """,
            new { correlationId });
    }

    private sealed record LogRowRecord(string SMessage, string SSeverity, int IEnvironmentId, int ICategoryId, string SCustomAttributes);
}
