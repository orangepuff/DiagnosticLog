namespace Diagnostics.NLog.Interfaces;

/// <summary>
/// Resolves <c>[dbo].[Environments]</c> names to ids, caching results and inserting rows that don't exist yet.
/// </summary>
/// <remarks>
/// Design doc §6: "resolve id by name once, cache, insert-if-missing".
/// </remarks>
public interface IEnvironmentResolver
{
    Task<int> ResolveIdAsync(string name, string? version, string? url, CancellationToken ct = default);
}
