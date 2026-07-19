using Diagnostics.NLog.Lookups.Resolvers;

namespace Diagnostics.Logs.UnitTests.NLog;

/// <summary>
/// Verifies the race-safe caching contract without a real SQL Server: concurrent calls for the
/// same name must coalesce into one in-flight resolution, and a failed resolution must evict its
/// cache entry so the next call gets a fresh attempt instead of a permanently cached fault.
/// </summary>
public class CategoryResolverTests
{
    // Bogus host — fails fast (no reachable SQL Server needed for these tests). Real coalescing
    // under concurrent load (same DB row, no duplicate insert) is covered by the integration tests
    // instead, since proving two calls share the same in-flight Task requires the resolution to
    // still be pending when the second call arrives — not reliably reproducible against a
    // synchronously-failing connection.
    private const string UnreachableConnectionString =
        "Server=diagnostics-resolver-tests-unreachable-host;Database=DiagnosticLogs;Connect Timeout=1;Encrypt=False;TrustServerCertificate=True;";

    [Fact]
    public void ResolveIdAsync_DifferentNames_GetIndependentTasks()
    {
        var resolver = new CategoryResolver(UnreachableConnectionString);

        var first = resolver.ResolveIdAsync("category-a");
        var second = resolver.ResolveIdAsync("category-b");

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task ResolveIdAsync_AfterFailure_EvictsCacheSoNextCallRetries()
    {
        var resolver = new CategoryResolver(UnreachableConnectionString);

        var failedTask = resolver.ResolveIdAsync("retry-category");
        await Assert.ThrowsAnyAsync<Exception>(() => failedTask);

        var retryTask = resolver.ResolveIdAsync("retry-category");

        Assert.NotSame(failedTask, retryTask);
        await Assert.ThrowsAnyAsync<Exception>(() => retryTask);
    }
}
