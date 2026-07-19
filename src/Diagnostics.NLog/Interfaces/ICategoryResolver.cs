namespace Diagnostics.NLog.Interfaces;

/// <summary>
/// Resolves <c>[dbo].[Categories]</c> names to ids, caching results and inserting rows that don't exist yet.
/// </summary>
/// <remarks>
/// Design doc §6: "resolve id by name once, cache, insert-if-missing".
/// </remarks>
public interface ICategoryResolver
{
    Task<int> ResolveIdAsync(string name, CancellationToken ct = default);
}
