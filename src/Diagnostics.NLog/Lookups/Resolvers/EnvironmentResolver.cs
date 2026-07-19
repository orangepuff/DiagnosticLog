using System.Collections.Concurrent;
using Dapper;
using Diagnostics.NLog.Interfaces;
using Microsoft.Data.SqlClient;

namespace Diagnostics.NLog.Lookups.Resolvers;

/// <inheritdoc cref="IEnvironmentResolver"/>
public sealed class EnvironmentResolver(string connectionString) : IEnvironmentResolver
{
    private readonly ConcurrentDictionary<string, Lazy<Task<int>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<int> ResolveIdAsync(string name, string? version, string? url, CancellationToken ct = default)
    {
        var lazy = _cache.GetOrAdd(
            name,
            key => new Lazy<Task<int>>(
                () => ResolveAndCacheAsync(key, version, url, ct),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    private async Task<int> ResolveAndCacheAsync(string name, string? version, string? url, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);

            var id = await connection.QuerySingleOrDefaultAsync<int?>(
                "SELECT TOP 1 iId FROM dbo.Environments WHERE sName = @name",
                new { name }).ConfigureAwait(false);

            if (id is null)
            {
                id = await connection.QuerySingleAsync<int>(
                    """
                    INSERT INTO dbo.Environments (sName, sVersion, sUrl)
                    OUTPUT INSERTED.iId
                    VALUES (@name, @version, @url)
                    """,
                    new { name, version, url }).ConfigureAwait(false);
            }

            return id.Value;
        }
        catch
        {
            _cache.TryRemove(name, out _);
            throw;
        }
    }
}
