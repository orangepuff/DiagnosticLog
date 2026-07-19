# DiagnosticLog

Reusable .NET diagnostics/logging library, extracted from the OCRWeb project so it can be
shared across solutions and published as a NuGet package.

## Packages

- **Diagnostics.Abstractions** — ports/contracts (`ITransactionLogger`, `ITransactionScope`,
  `TransactionRecord`, `ICorrelationContext`). No dependency on NLog, EF Core, or ASP.NET Core.
- **Diagnostics.NLog** — NLog-backed implementation: DB-driven configuration (Dapper),
  `SqlBulkCopy`-backed custom targets for `[Logs]`/`[Transactions]`, and the `ILogger`/
  `ITransactionLogger` bridge.
- **Diagnostics.AspNetCore** — optional ASP.NET Core integration: per-request transaction
  middleware and `X-Correlation-ID` propagation (inbound middleware + outbound
  `DelegatingHandler`).

See [docs/diagnostics-logging-design.md](docs/diagnostics-logging-design.md) for the full
design and [docs/diagnostics-logs-schema.sql](docs/diagnostics-logs-schema.sql) for the
database schema (idempotent, safe to re-run).

## Building

```powershell
dotnet build DiagnosticLog.slnx
dotnet test tests/unit/Diagnostics.Logs.UnitTests
dotnet test tests/integration/Diagnostics.Logs.IntegrationTests  # needs a reachable SQL Server, see appsettings.json
```
