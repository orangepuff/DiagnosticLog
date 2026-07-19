using Dapper;
using Microsoft.Data.SqlClient;

namespace Diagnostics.Logs.IntegrationTests;

/// <summary>
/// Exercises <see cref="Diagnostics.NLog.Interfaces.ICategoryResolver"/> against a real
/// DiagnosticLogs database, including the concurrent-resolution race the caching design in
/// CategoryResolver exists to close.
/// Requires a reachable SQL Server — see <see cref="DiagnosticsDatabaseFixture"/>.
/// </summary>
[Collection(nameof(DiagnosticsDatabaseCollection))]
public sealed class CategoryResolverIntegrationTests(DiagnosticsDatabaseFixture fixture)
{
    [Fact]
    public async Task ResolveIdAsync_NewName_InsertsExactlyOneRow()
    {
        var name = $"integration-test-category-{Guid.NewGuid():N}";

        var id = await fixture.CategoryResolver.ResolveIdAsync(name, CancellationToken.None);

        Assert.True(id > 0);
        Assert.Equal(1, await CountRowsAsync(name));
    }

    [Fact]
    public async Task ResolveIdAsync_ConcurrentCallsForSameNewName_InsertExactlyOneRowAndAgreeOnId()
    {
        var name = $"integration-test-category-race-{Guid.NewGuid():N}";

        var ids = await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(_ => fixture.CategoryResolver.ResolveIdAsync(name, CancellationToken.None)));

        Assert.All(ids, id => Assert.Equal(ids[0], id));
        Assert.Equal(1, await CountRowsAsync(name));
    }

    private async Task<int> CountRowsAsync(string name)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);

        return await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.Categories WHERE sName = @name",
            new { name });
    }
}
