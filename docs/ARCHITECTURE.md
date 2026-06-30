# Architecture

A deeper look at the design decisions behind the curated code in this repository.

## Separation of powers

The system is not a monolith with a UI bolted on; it is several single-responsibility surfaces coordinating through one authoritative API.

| Surface | Technology | Responsibility | Explicitly cannot |
|---------|-----------|----------------|-------------------|
| **POS Register** | .NET / WinForms, local SQLite | Execute sales at the edge; sync when connected | Manage inventory or prices |
| **Back-Office Console** | Blazor Server | Inventory, staff, pricing, analytics — every business decision | Process a sale; touch the database directly |
| **Commerce API** | ASP.NET Core | Enforce all business rules, reconcile sync, resolve conflicts | — (it is the source of truth) |
| **Background Jobs** | Hangfire | Deferred/scheduled work (reporting, notifications, maintenance) | Bypass the service layer's rules |

Business logic lives in a single services layer. The POS references it directly for edge performance; the back-office reaches it only over HTTP. There is exactly one implementation of any given rule.

### Dependency direction

```
Core  →  (nothing)
Shared →  Core
Data   →  Core + Shared
Services → Core + Data + Shared
API     → Services + Shared
```

`Core` (domain entities, enums, interfaces) depends on nothing. Dependencies only ever point inward. A change that violates this direction is rejected on principle.

## Offline-first and the closed loop

The defining constraint: **the till must keep selling for up to 30 days with no internet, then reconcile perfectly.**

1. **At the edge**, a sale is written to a local SQLite store and immediately produces a receipt. Sale, payment(s), and stock movement are recorded as one connected event — the "closed loop."
2. A **conflict hash** is computed over a canonical, culture-invariant serialization of the sale (total, timestamp, register, sequence, line-item JSON, split flag) using SHA-256. See [`SaleIntegrityUtility.cs`](../src/BMS.Shared/Utilities/SaleIntegrityUtility.cs).
3. **When connectivity returns**, sales are pushed in batches with exponential backoff. The server is *cloud-authoritative*: it owns the final truth.

### What makes the sync safe

The single-sale ingestion path ([`SyncService.SaleSync.cs`](../src/BMS.Services/Services/SyncService.SaleSync.cs)) is the system's most important method. In order, it:

- **Validates** the payload (FluentValidation).
- **Zero-trust checks** the register and branch against server records and the authenticated user's claims — a tampered client cannot inject a sale into another branch.
- **Deduplicates by GUID.** The sale's GUID is its authoritative identity. Re-pushing an already-synced GUID returns `AlreadySynced` — never an error, never a duplicate row.
- **Guards the receipt number.** The human-readable receipt number is a *display* key under a unique index, not the sync discriminant. A number reused under a *different* GUID is a genuine cross-register conflict; it is dead-lettered as a terminal validation error for manual intervention, not retried forever.
- **Verifies integrity.** The server recomputes the SHA-256 hash over the byte-for-byte payload. A mismatch means tampering or corruption → rejected and logged.
- **Writes atomically.** Inside one transaction: deserialize line items, validate foreign keys, insert the sale + items + payments, deduct inventory, write prescription-override audit rows, and deduct batch lots. A race on the unique index is caught and reclassified as the same terminal conflict rather than a retryable fault.

**Cloud-authoritative reconciliation** means a transaction that physically happened at the till always settles. Anomalies — negative stock from an offline oversell, an over-dispensed batch lot, a bypassed prescription gate — are **recorded for oversight, not rejected**. The cloud reconciles reality; it does not deny it. (The interactive paths — the till and the online pre-commit — still hard-block these; only the reconciliation path is permissive, because the event already occurred.)

## Multi-tenancy

Tenant isolation is a property of the schema, not of developer discipline. Every tenant-scoped entity carries the same EF Core global query filter:

```csharp
modelBuilder.Entity<Sale>()
    .HasQueryFilter(e => _currentTenantService.IsSuperAdmin
                      || e.TenantId == _currentTenantService.TenantId);
```

A query that forgets to filter by tenant still cannot see another tenant's rows. The only bypass is an explicit, audited `IsSuperAdmin` flag used by the vendor control plane. `TenantId` is stamped automatically on insert inside `SaveChanges`, so application code can never forget to set it. See [`ApplicationDbContext.MultiTenancy.cs`](../src/BMS.Data/ApplicationDbContext.MultiTenancy.cs).

## Financial precision

- All money is `decimal` (`decimal(18,2)`). `float`/`double` are never used for monetary values.
- Quantities are `decimal(18,3)` to support weighed and loose goods (e.g. 0.75 kg).
- Payments form an **append-only ledger** — financial state (total paid, balance due, status) is *derived* from payment records, never mutated in place. Each payment carries an idempotency key so a retried sync never double-charges.

See [`Sale.cs`](../src/BMS.Core/Entities/Sale.cs), [`SaleItem.cs`](../src/BMS.Core/Entities/SaleItem.cs), [`SalePayment.cs`](../src/BMS.Core/Entities/SalePayment.cs).

## Concurrency & data model conventions

- **Optimistic concurrency** uses PostgreSQL's built-in `xmin` system column — no extra column, no migration. A stale write is rejected automatically.
- **Every ID is a `Guid`**, generated client-side so the edge can mint identities offline.
- **Soft deletes** via an `IsActive` flag; rows are deactivated, not destroyed.
- **All timestamps are UTC.** Payment events use `DateTimeOffset` to avoid timezone drift between a local till and a UTC cloud.
- **Immutable snapshots.** A sale captures customer name, branch address, and tax breakdown at the moment of sale, so a historical receipt reproduces exactly even after the underlying records change.

## Reads: server-side everything

Reporting queries filter, count, order, and page **in the database**. Composable `IQueryable` extensions build the expression tree before materialization, so a large table never lands in memory first. Aggregation is database-side; pagination is capped. See [`QueryableExtensions.cs`](../src/BMS.Data/Extensions/QueryableExtensions.cs), [`ModelBuilderExtensions.cs`](../src/BMS.Data/Extensions/ModelBuilderExtensions.cs) (≈50 performance indexes), and [`SalesService.Query.cs`](../src/BMS.Services/Services/SalesService.Query.cs).

## API conventions

- Every endpoint returns a uniform `ApiResponseDto<T>` envelope, including unauthenticated 401s (never an empty body).
- Error responses carry a machine-readable error code plus trace/correlation IDs.
- Mutating verbs require CSRF validation under cookie auth; the API also supports JWT bearer auth for the POS.
- The OpenAPI contract is generated in CI and **diffed against a committed baseline** — a breaking change blocks merge. See [`SalesController.cs`](../src/BMS.WebAPI/Controllers/SalesController.cs) and [`ci-pipeline.yml`](ci-pipeline.yml).

## Testing strategy

- **Unit tests** cover the services layer.
- **Integration tests** run the full HTTP → service → PostgreSQL stack against a **real PostgreSQL container**, never an in-memory provider — because the guarantees under test (unique constraints, `xmin` concurrency, transactional rollback) only exist in the real engine. Tests run sequentially against a schema built once, with per-test data isolation via `TRUNCATE … RESTART IDENTITY`. See [`SyncIdempotencyTests.cs`](../src/BMS.IntegrationTests/Tests/SyncIdempotencyTests.cs).
