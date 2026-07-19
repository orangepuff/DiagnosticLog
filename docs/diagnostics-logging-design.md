# Diagnostics Logging — Design (design phase, no code yet)

Reusable diagnostic + transaction logging built on **NLog**, with configuration stored
centrally in a shared **`DiagnosticLogs`** database. Two sinks: `[dbo].[Logs]` for severity-based
line logs (with exception + severity) and `[dbo].[Transactions]` for operation telemetry.

**Reuse goal:** product-neutral. Root namespace is **`Diagnostics.*`** with **zero dependency on
`OCRWeb.*`**, so it can be lifted into its own repo and published as an internal NuGet package
(private feed) and consumed by other solutions.

> Status: **implemented, extracted to its own repo.** Originally built in-repo under
> `OCRWeb`'s `src/Shared/Diagnostics.*`; now lives in this standalone `DiagnosticLog` repo
> (`src/Diagnostics.*`) and is consumed by `OCRWeb` as a NuGet package
> (`Diagnostics.Abstractions`/`.NLog`/`.AspNetCore`). All decisions in §9 are settled:
> `Diagnostics.Abstractions` (ports: `ICorrelationContext`/`CorrelationContext`, `ITransactionLogger`,
> `ITransactionScope`, `TransactionRecord`, `DiagnosticsOptions`), `Diagnostics.NLog` (DB-backed
> config via Dapper, `LogsTarget`/`TransactionsTarget` custom targets → bounded/batched →
> `SqlBulkCopy`, `AddDiagnostics`), and `Diagnostics.AspNetCore` (`CorrelationIdMiddleware`,
> `TransactionMiddleware`, `CorrelationIdDelegatingHandler`, `UseDiagnostics`). Wired into
> `OCRWeb.API` against a `DiagnosticLogs` connection string. One implementation deviation from a
> literal reading of §5: `DbConfigProvider` parses only the `<rules>` section of the DB-stored NLog
> XML (minlevel per logger-name pattern); `<targets>` are NOT read from the DB — the two custom
> targets are constructed in code via `AddDiagnostics` so they can take constructor-injected
> dependencies (connection string, `IEnvironmentResolver`/`ICategoryResolver`, `ICorrelationContext`) instead of
> NLog's XML-driven property binding. This matches the intent of §5's "split of concerns" bullet
> (infra wiring in code, levels/rules in the DB) even though it narrows what the DB XML controls.

---

## 1. Not a bounded context — a shared observability library

Like `OCRWeb.Pdf`, logging is a cross-cutting technical concern, not a domain. It differs in that
it must be reusable **across solutions**, so it is fully decoupled from OCRWeb and named neutrally.

## 2. Projects (ports & adapters)

```
Diagnostics.Abstractions      (contract/port — no NLog, EF, or ASP.NET)
    │   ITransactionLogger, ITransactionScope : IDisposable,
    │   TransactionRecord (maps to [Transactions]), ICorrelationContext.
    │   App code + other libs depend ONLY on this.
    ▼
Diagnostics.NLog              (adapter)
    │   - DbConfigProvider: load NLog XML from [Configurations] by logger+environment
    │   - LogsTarget         : NLog custom target → SqlBulkCopy → [Logs]  (async + buffered)
    │   - TransactionsTarget : NLog custom target → SqlBulkCopy → [Transactions] (async + buffered)
    │   - ITransactionLogger implementation + ILogger bridge (NLog.Extensions.Logging)
    │   - Environment/Category id resolver (cached)
    │   - AddDiagnostics(IServiceCollection, options) DI extension
    ▼
Diagnostics.AspNetCore        (integration — optional per consumer)
        per-request transaction middleware (url/user/correlation/duration,
        metadata only — never auto-captures bodies) + X-Correlation-ID
        propagation (inbound read-or-generate; outbound DelegatingHandler)
```

Each app calls `AddDiagnostics(...)` at its composition root (like `AddPdfEngine()`), and web apps
add the middleware. **Target framework: `net10.0` for all Diagnostics projects** (every consumer is
.NET 10).

## 3. Write path — the app does not decide *when* to write

Level-based logs (Info/Warning/Error) happen at unpredictable times. The app only calls the API;
**NLog owns write timing** via its target pipeline. There are **two independent sinks**:

```
                                       ┌► LogsTarget         ─► SqlBulkCopy → [Logs]
app code ─► ILogger  ──────────────────┘   (severity + exception line logs)
         ─► ITransactionScope ─────────┐
                                       └► TransactionsTarget ─► SqlBulkCopy → [Transactions]
                                           (operation telemetry: duration, req/resp, spans)

   both targets: AsyncWrapper (never blocks the request) + batching (flush on count/time)
```

- **`[Logs]` — Info/Warn/Error line logs**: `ILogger` bridged to NLog
  (`NLog.Extensions.Logging`). NLog level → `sSeverity`, exception → `sException`. minLevel/rules
  come from the DB config, so verbosity is tunable per environment without redeploy. Each row
  optionally carries `sTransactionId` = the enclosing transaction (from the ambient context).
  `LogsTarget.Write` never reads the ambient `ICorrelationContext` directly — by the time it runs
  (on `AsyncTargetWrapper`'s own drain thread) the caller's `AsyncLocal`-backed state is gone.
  `AmbientContextCapturingTargetWrapper` sits between the rule and `AsyncTargetWrapper(logsTarget)`
  and snapshots correlation id/transaction id/category onto the event synchronously, on the
  caller's own thread, before the async wrapper can defer it.
- **`[Transactions]` — rich telemetry**: `ITransactionScope` (IDisposable), opened via the single
  entry point `ITransactionLogger.BeginTransaction(category, message)` — measures duration and
  flushes one row on dispose. There is **no separate functional-area helper**; the transaction sets
  the category, and line logs written inside it inherit it.

```csharp
using var tx = txLogger.BeginTransaction(category: "UploadPdf", message: "POST /pdf-files");
tx.SetUser(user);
tx.RequestJson(requestJson);      // body persisted ONLY because this is called
// ... do work ...
tx.ResponseJson(responseJson);    // dispose → compute duration → enqueue row
```

Body capture is **explicit, per transaction** — never automatic. Format-specific methods map to the
matching column so no guessing:

| Method | Column |
|---|---|
| `RequestJson` / `RequestXml` / `RequestText` | `sRequestJson` / `xRequestXml` / `sRequestText` |
| `ResponseJson` / `ResponseXml` / `ResponseText` | `sResponseJson` / `xResponseXml` / `sResponseText` |

The ASP.NET middleware captures **metadata only** (url, baseUrl, user, duration, correlation,
status) and does **not** read request/response bodies — bodies exist in `[Transactions]` only where
the code opted in via these methods. A size cap + redaction hook still apply to what is captured.

## 4. Data access

| Concern | Tool | Rationale |
|---|---|---|
| Writing logs (lines + transactions) | **custom NLog target → SqlBulkCopy**, async + buffered | unpredictable timing / high volume → NLog batches; batches suit the clustered columnstore |
| Reading `[Configurations]` XML; resolving `[Environments]`/`[Categories]` ids | **Dapper** (small queries, cached) | lightweight, at startup + poll |
| — | **No EF Core** | DBA-owned shared schema; columnstore + row-by-row EF inserts are the wrong fit |

## 5. Config stored in the DB

- `Configurations.xValue` holds NLog XML (`<nlog><targets/><rules/>`), keyed by `sLoggerName`
  (app/component) and `iEnvironmentId` (`NULL` = global default).
- `DbConfigProvider` at startup: resolve the environment, load default + environment-specific XML,
  apply via `LogManager.Configuration = new XmlLoggingConfiguration(reader)`.
- **Hot reload:** poll `dtUpdatedTime` (e.g. every 30–60s); reapply when it changes.
- **Split of concerns:** infrastructure wiring (connection string, the Transactions target,
  environment identity) is set in code via `AddDiagnostics` options; levels/rules/filters live in
  the DB XML so ops can tune them centrally.
- **Fallback:** if `DiagnosticLogs` is unreachable, fall back to a local file target from an
  embedded config — logging must never take the app down.

## 6. Two sinks: `[Logs]` and `[Transactions]`

Line logs and operation telemetry are stored separately; a log line links back to its enclosing
transaction via `Logs.sTransactionId` (soft link — column only, no enforced FK, since writes are
async/batched). Prefix convention: `i`=int, `s`=string/guid, `dt`=datetime, `x`=xml-as-text.

**`[Logs]`** — one row per `ILogger` event:

| Field | Source |
|---|---|
| `iId` (bigint identity) | DB-generated (omit from SqlBulkCopy) |
| `sTransactionId` | current transaction id from ambient context, or null |
| `iEnvironmentId` | resolved app/env id |
| `iCategoryId` | **functional area** (e.g. `UploadPdf`), resolved by precedence below — NOT the logger type name |
| `sCorrelationId` | from `ICorrelationContext` |
| `dtTimeLogged` | event time |
| `sMessage` / `sException` | rendered message / formatted exception |
| `sSeverity` | NLog level → `Trace/Debug/Info/Warn/Error/Fatal` (fix the vocabulary) |
| `sUser` | current user |
| `sCustomAttributes` | JSON of structured properties, incl. the .NET logger name (type) |

**`[Transactions]`** — one row per `ITransactionScope`:

| Field | Source |
|---|---|
| `sId` / `sParentId` | scope id / parent scope id (span tree) |
| `iEnvironmentId` / `iCategoryId` | env id / **functional area** id (e.g. `UploadPdf`) |
| `sCorrelationId` | request/header correlation |
| `sMessage` | operation summary |
| `sUrl` / `sBaseUrl` | request url / base url |
| `dtStartTime` / `iDuration` | start / measured ms (on dispose) |
| `xRequest*/sRequest*/xResponse*/sResponse*` | only when code calls `tx.Request*/Response*` (size-capped, redacted) |
| `sUser` | current user |
| `sCustomAttributes` | extra structured data |
| `sSql` | captured SQL if enabled |

`Categories`/`Environments` are lookups shared by both tables: resolve id by name once, cache,
insert-if-missing.

**Ambient context** (`ICorrelationContext`, AsyncLocal) carries the `CorrelationId`, the
**current `TransactionId`**, and the **current functional area (`Category`)**, so a line log written
inside a transaction scope is stamped automatically with `sTransactionId` and the scope's category.

**Functional area comes from the transaction you are in.** `BeginTransaction(category, ...)` is the
single scope entry point and sets it — there is no separate log-only categorizer. `LogsTarget`
resolves `iCategoryId` by precedence:

1. the ambient transaction's `Category` (if inside a `BeginTransaction` scope), else
2. a **guardrail** value only to satisfy `NOT NULL` (a reserved `"(none)"`) — surfaced as a metric
   so un-categorized logs get noticed rather than silently defaulted.

```csharp
using var tx = txLogger.BeginTransaction("Reindex", "nightly reindex");
logger.LogInformation("received {Bytes} bytes", size);   // inherits category "Reindex" + tx id
```

## 7. Reliability (telemetry on the hot path)

- Logging must **never throw** into the app: `throwExceptions=false`, custom target swallows/handles
  internal errors, DB outage → local file fallback.
- Bounded in-memory queue with a drop policy to avoid unbounded memory growth under load.
- **Default profile (balanced, override via `AddDiagnostics` options):** flush at **500 rows** or
  **every 2s** (whichever first); **bounded queue ~10,000 rows**; on overflow **drop + increment a
  metric** (never block the request); on DB failure, short retry then fall back to the local file
  target.
- Separate database (`DiagnosticLogs`) and connection string so logging load never contends with
  the application database.

## 8. Reuse mechanics

- Namespace `Diagnostics.*`, no `OCRWeb.*` references. Originally developed in-repo under
  OCRWeb's `src/Shared/Diagnostics.*`; extracted into this standalone `DiagnosticLog` repo
  (`src/Diagnostics.*`) and packed as NuGet. Keeping zero OCRWeb dependencies made the
  extraction a lift-and-shift.
- Keep `Diagnostics.Abstractions` stable and versioned; consumers reference the package, call
  `AddDiagnostics(...)`, and (for web) add the middleware.

## 9. Decisions (all settled)

1. **Settled: `Logs.sSeverity` uses NLog level names** — `Trace/Debug/Info/Warn/Error/Fatal`
   (the values NLog actually writes; `ILogger` `Information/Warning/Critical` map to
   `Info/Warn/Fatal`). Level location is settled too: `[Logs]` for line logs, `[Transactions]`
   for telemetry.
2. **Settled: bodies are captured explicitly per transaction** via `tx.RequestJson/Xml/Text` and
   `tx.ResponseJson/Xml/Text`; the middleware never auto-captures bodies. Size cap + redaction hook
   still apply. Nit: default max body size before truncation.
3. **Settled: correlation via `X-Correlation-ID`.** Inbound: read the header, else generate a guid
   (non-guid incoming value kept in `sCustomAttributes`). Outbound: an opt-in `DelegatingHandler`
   forwards the current id on `HttpClient` calls. `sParentId` = in-process nested scopes for now
   (cross-service span linking is future).
4. **Settled: `net10.0`** for all Diagnostics projects (all consumers are .NET 10).
5. **Settled: extracted to its own repo (`DiagnosticLog`) + packed as NuGet.** Originally
   built in-repo under OCRWeb's `src/Shared/Diagnostics.*`, separate from `src/Backend`.
6. **Settled: balanced profile** — flush 500 rows / 2s, bounded queue ~10k, drop+metric on
   overflow, DB-failure fallback to local file. Overridable via `AddDiagnostics` options.
7. **Settled: `Categories` = functional area** (e.g. `UploadPdf`, `CropPdf`, `OcrRun`), not the
   logger name. Set by the enclosing `BeginTransaction(category, …)` — the single scope entry point
   (no separate functional-area helper). Resolution precedence = ambient transaction category →
   `"(none)"` guardrail (to satisfy `NOT NULL`, surfaced as a metric).
