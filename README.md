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
// onto transaction spans (see "Overriding user attribution" below).
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

### 5. Overriding user attribution (optional)

By default, `TransactionMiddleware` stamps `sUser` on the request's transaction span straight
from ASP.NET Core's own auth principal:

```csharp
// inside TransactionMiddleware, runs automatically — no code needed on your side
if (context.User.Identity?.IsAuthenticated == true)
{
    scope.SetUser(context.User.Identity.Name);
}
```

That's often not what you want — e.g. you may want an internal numeric user id instead of
whatever name is on the claims principal, or you want every span opened *anywhere* during a
request (not just `TransactionMiddleware`'s own root span for the request) to automatically
carry the current user/URL, without every call site calling `SetUser`/`SetUrl` itself. Do this by
decorating `ITransactionLogger` and re-registering it *after* `AddDiagnostics(...)`, so DI resolves
your decorator instead of the library's own implementation:

```csharp
public sealed class RequestContextTransactionLogger(
    ITransactionLogger inner, ICurrentUser currentUser, IHttpContextAccessor httpContextAccessor)
    : ITransactionLogger
{
    public ITransactionScope BeginTransaction(string category, string? message = null)
    {
        var scope = inner.BeginTransaction(category, message);

        try
        {
            scope.SetUser(currentUser.UserId.ToString());
        }
        catch (InvalidOperationException)
        {
            // No resolvable user for this request — leave sUser unset rather than fail
            // the request just because logging couldn't attribute an actor.
        }

        var request = httpContextAccessor.HttpContext?.Request;
        if (request is not null)
        {
            scope.SetUrl(
                url: $"{request.Path}{request.QueryString}",
                baseUrl: $"{request.Scheme}://{request.Host}");
        }

        return scope;
    }
}
```

Note this `SetUrl` call does real work beyond what `TransactionMiddleware` already does: the
middleware only sets the URL on its own root span for the request. Any *nested* span your
application code opens later (e.g. `transactionLogger.BeginTransaction("UploadPdf", ...)` inside
a command handler) doesn't inherit it — going through this decorator, every span gets it, root
or nested.

For a request `POST https://localhost:7101/api/pdf-files/upload?projectId=abc123`:

| `HttpRequest` property | Value |
|---|---|
| `request.Scheme` | `https` |
| `request.Host` | `localhost:7101` (port included) |
| `request.Path` | `/api/pdf-files/upload` |
| `request.QueryString` | `?projectId=abc123` (already includes the leading `?`; empty string, not `"?"`, when there's no query) |

...which `SetUrl` receives as:

```csharp
url:     "/api/pdf-files/upload?projectId=abc123"   // {Path}{QueryString}
baseUrl: "https://localhost:7101"                    // {Scheme}://{Host}
```

`url`/`baseUrl` are kept separate rather than one combined string so you can filter/group by
`baseUrl` later (e.g. "every request that hit this host") without parsing the full URL back apart.

```csharp
builder.Services.AddDiagnostics(options => { /* ... */ });

// Must come after AddDiagnostics — overrides its base ITransactionLogger registration.
// Scoped, not singleton, because ICurrentUser/IHttpContextAccessor are request-scoped.
builder.Services.AddScoped<ITransactionLogger>(sp => new RequestContextTransactionLogger(
    sp.GetRequiredService<TransactionLoggerImplementation>(),
    sp.GetRequiredService<ICurrentUser>(),
    sp.GetRequiredService<IHttpContextAccessor>()));
```

`ICurrentUser` here is your own app's abstraction, not part of this library — swap in whatever
represents "who is making this request" for you. Every `BeginTransaction` call anywhere in the
app, direct or through `TransactionMiddleware`'s own per-request span, now goes through this
decorator and gets `sUser`/`sUrl` attributed consistently.

### 6. Propagate correlation ids on outbound HTTP calls (optional)

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
