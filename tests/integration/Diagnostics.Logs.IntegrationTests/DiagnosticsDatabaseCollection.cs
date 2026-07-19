namespace Diagnostics.Logs.IntegrationTests;

/// <summary>
/// Shares one <see cref="DiagnosticsDatabaseFixture"/> (and its one-time schema setup) across every
/// test class in this collection, instead of each class provisioning its own in parallel.
/// </summary>
/// <remarks>
/// xUnit runs distinct test classes as distinct collections in parallel by default; without this,
/// each <c>IClassFixture&lt;DiagnosticsDatabaseFixture&gt;</c> class would spin up its own fixture
/// concurrently, and the schema script's <c>IF NOT EXISTS ... ADD CONSTRAINT</c> guards race —
/// two connections can both see "doesn't exist yet" and both try to create it.
/// </remarks>
[CollectionDefinition(nameof(DiagnosticsDatabaseCollection))]
public sealed class DiagnosticsDatabaseCollection : ICollectionFixture<DiagnosticsDatabaseFixture>
{
}
