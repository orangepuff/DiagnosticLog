# DiagnosticLog

Reusable .NET diagnostics/logging library: NLog-backed line logging plus structured
operation/transaction telemetry, both written to a shared SQL Server database. Framework-neutral
at the core — the ASP.NET Core integration is opt-in — so it can be dropped into any .NET
service, not just web APIs.

## Packages

| NuGet PackageId | Namespace | What it's for |
|---|---|---|
| `Orangepuff.Diagnostics.Abstractions` | `Diagnostics.Abstractions` | Ports/contracts only: `ITransactionLogger`, `ITransactionScope`, `TransactionRecord`, `ICorrelationContext`, `DiagnosticsOptions`. No dependency on NLog, EF Core, or ASP.NET Core. |
| `Orangepuff.Diagnostics.NLog` | `Diagnostics.NLog` | NLog-backed implementation: DB-driven configuration (Dapper), `SqlBulkCopy`-backed custom targets for `[Logs]`/`[Transactions]`, and the `ILogger`/`ITransactionLogger` bridge. Pulls in `Abstractions`. |
| `Orangepuff.Diagnostics.AspNetCore` | `Diagnostics.AspNetCore` | Optional ASP.NET Core integration: per-request transaction middleware and `X-Correlation-ID` propagation (inbound middleware + outbound `DelegatingHandler`). Pulls in `Abstractions`. |

The NuGet `PackageId`s carry a `Orangepuff.` prefix (nuget.org rejected the plain `Diagnostics.*`
names as a reserved ID prefix owned by someone else); the C# namespaces stay plain `Diagnostics.*`,
so nothing in your code needs that prefix — only your `.csproj`/`PackageReference` entries do.

See [docs/diagnostics-logging-design.md](docs/diagnostics-logging-design.md) for the full design
and [docs/diagnostics-logs-schema.sql](docs/diagnostics-logs-schema.sql) for the database schema
(idempotent — safe to re-run).

## Prerequisites

A reachable SQL Server database (any name) with the schema from
[docs/diagnostics-logs-schema.sql](docs/diagnostics-logs-schema.sql) applied — it creates
`Environments`, `Configurations`, `Categories`, `Transactions`, and `Logs` tables. Run it once
against your target database:

```powershell
sqlcmd -S <server> -d <your-diagnostics-db> -i docs/diagnostics-logs-schema.sql
```

Keep this database separate from your application's own database — logging/telemetry write
volume shouldn't contend with your app's normal workload.

## Installing

```powershell
# Any app that just wants to log (console app, worker service, non-web API, ...)
dotnet add package Orangepuff.Diagnostics.Abstractions
dotnet add package Orangepuff.Diagnostics.NLog

# Add this too if you're building an ASP.NET Core app and want per-request
# transaction spans + X-Correlation-ID propagation
dotnet add package Orangepuff.Diagnostics.AspNetCore
```

## Quick start

### 1. Register the pipeline

```csharp
using Diagnostics.NLog.DependencyInjection;

builder.Services.AddDiagnostics(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("DiagnosticLogs")
        ?? throw new InvalidOperationException("ConnectionStrings:DiagnosticLogs is not configured.");
    options.LoggerName = builder.Configuration["Diagnostics:LoggerName"] ?? "MyApp";
    options.EnvironmentName = builder.Configuration["Diagnostics:EnvironmentName"] ?? "DEV";
});
```

`appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DiagnosticLogs": "Server=...;Database=DiagnosticLogs;..."
  },
  "Diagnostics": {
    "LoggerName": "MyApp",
    "EnvironmentName": "DEV"
  }
}
```

`LoggerName` is a key into `[dbo].[Configurations]` — it's how you scope NLog minlevel rules
(stored in the DB, hot-reloadable, no redeploy needed) to this specific app/component.

### 2. ASP.NET Core only: wire up the middleware

```csharp
using Diagnostics.AspNetCore.DependencyInjection;

builder.Services.AddDiagnosticsAspNetCore();

// ...

// Must run AFTER authentication middleware if you plan to stamp the current user
// onto transaction spans (see "Attaching the current user" below).
app.UseDiagnostics();
```

`UseDiagnostics()` adds, in order: correlation-id resolution (reads/generates
`X-Correlation-ID`) then a per-request transaction span (metadata only — method, path, status
code; never auto-captures request/response bodies).

### 3. Log normally via `ILogger`

Nothing special — inject `ILogger<T>` as usual. Lines written while a transaction scope (below)
is open automatically inherit its transaction id and category.

```csharp
public sealed class MyService(ILogger<MyService> logger)
{
    public void DoWork()
    {
        logger.LogInformation("Doing the thing");
    }
}
```

### 4. Wrap an operation in a transaction span

```csharp
using Diagnostics.Abstractions.Interfaces;

public sealed class MyService(ITransactionLogger transactionLogger)
{
    public async Task RunAsync()
    {
        using var tx = transactionLogger.BeginTransaction(category: "RunReport", message: "Nightly report run");

        tx.SetUser("system");
        tx.RequestJson(JsonSerializer.Serialize(new { reportDate = DateOnly.FromDateTime(DateTime.UtcNow) }));

        // ... do the work; any ILogger lines in here inherit tx's id/category ...

        tx.ResponseJson(JsonSerializer.Serialize(new { rowsProcessed = 42 }));
    } // Dispose() flushes one row to [dbo].[Transactions]
}
```

Body capture (`RequestJson`/`ResponseJson`/`RequestXml`/etc.) is always explicit and opt-in per
call — nothing is auto-captured, so you decide exactly what's worth persisting and never leak a
sensitive body by accident.

### 5. Propagate correlation ids on outbound HTTP calls (optional)

```csharp
using Diagnostics.AspNetCore.DependencyInjection;

builder.Services.AddHttpClient<MyDownstreamClient>()
    .AddDiagnosticsCorrelationPropagation();
```

Forwards the current request's `X-Correlation-ID` on every outbound call made through that
`HttpClient`, so a trace can be followed across service boundaries.

## Publishing a new version

Releases go out via GitHub Actions using [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
(OIDC — no API key stored anywhere):

1. Bump `<Version>` in [`Directory.Build.props`](Directory.Build.props) if you want the change reflected in local `dotnet pack` runs too (the CI workflow derives the actual published version from the branch name regardless, so this step alone doesn't trigger anything).
2. Create and push a branch named `Release/vX.Y.Z` (matching [semver](https://semver.org)) off `main`:
   ```powershell
   git checkout -b Release/v1.2.3 main
   git push -u origin Release/v1.2.3
   ```
3. GitHub Actions ([`.github/workflows/publish.yml`](.github/workflows/publish.yml)) picks up the push, extracts `1.2.3` from the branch name, packs all three projects at that version, and pushes them to nuget.org.
4. Check the run at `https://github.com/orangepuff/DiagnosticLog/actions`.

`main` itself requires a pull request to merge — the `Release/*` branch is only ever cut from
an already-reviewed `main`, so nothing gets published without having gone through review first.

## Building locally

```powershell
dotnet build DiagnosticLog.slnx
dotnet test tests/unit/Diagnostics.Logs.UnitTests
dotnet test tests/integration/Diagnostics.Logs.IntegrationTests  # needs a reachable SQL Server, see appsettings.json
```
