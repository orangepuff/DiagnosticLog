using Dapper;
using Microsoft.Data.SqlClient;

namespace Diagnostics.Logs.IntegrationTests;

/// <summary>
/// Exercises <see cref="Diagnostics.NLog.Interfaces.IEnvironmentResolver"/> against a real
/// DiagnosticLogs database, including the concurrent-resolution race the caching design in
/// EnvironmentResolver exists to close.
/// Requires a reachable SQL Server — see <see cref="DiagnosticsDatabaseFixture"/>.
/// </summary>
[Collection(nameof(DiagnosticsDatabaseCollection))]
public sealed class EnvironmentResolverIntegrationTests(DiagnosticsDatabaseFixture fixture)
{
    [Fact]
    public async Task ResolveIdAsync_NewName_InsertsExactlyOneRow()
    {
        var name = $"integration-test-env-{Guid.NewGuid():N}";

        var id = await fixture.EnvironmentResolver.ResolveIdAsync(name, "1.0.0", "https://example.test", CancellationToken.None);

        Assert.True(id > 0);
        Assert.Equal(1, await CountRowsAsync(name));
    }

    [Fact]
    public async Task ResolveIdAsync_ConcurrentCallsForSameNewName_InsertExactlyOneRowAndAgreeOnId()
    {
        var name = $"integration-test-env-race-{Guid.NewGuid():N}";

        var ids = await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(_ => fixture.EnvironmentResolver.ResolveIdAsync(name, "1.0.0", "https://example.test", CancellationToken.None)));

        Assert.All(ids, id => Assert.Equal(ids[0], id));
        Assert.Equal(1, await CountRowsAsync(name));
    }

    private async Task<int> CountRowsAsync(string name)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);

        return await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.Environments WHERE sName = @name",
            new { name });
    }
}
