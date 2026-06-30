// ─────────────────────────────────────────────────────────────────────────────
//  PUBLIC SHOWCASE — CURATED EXCERPT (not the full production file)
//
//  This is a focused extract of the platform's EF Core DbContext, trimmed to the
//  parts that demonstrate the multi-tenant isolation model. In the real codebase
//  this type spans ~1,500 lines: ~50 per-entity `Configure*` methods, full index
//  configuration, and the complete DbSet surface. Those are elided here with
//  "// … omitted" markers so the isolation strategy reads clearly on its own.
//
//  What this excerpt shows:
//    1. A single ICurrentTenantService injected into the context — the ambient
//       tenant identity for the current request / POS session.
//    2. Global query filters: EVERY tenant-scoped entity is filtered by TenantId
//       at the database level, so a tenant physically cannot read another tenant's
//       rows — even if a query forgets a WHERE clause. A super-admin flag is the
//       single, audited bypass used by the Vendor control plane.
//    3. PostgreSQL `xmin` as a zero-migration optimistic-concurrency token.
//    4. SaveChanges overrides that stamp audit fields and the TenantId on insert,
//       so application code never has to remember to set it.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using BMS.Core.Entities;
using BMS.Core.Entities.Base;
using BMS.Data.Services.Interfaces;

namespace BMS.Data
{
    /// <summary>
    /// Main database context for the platform.
    /// Implements multi-tenant data isolation using EF Core global query filters.
    /// </summary>
    public class ApplicationDbContext : DbContext
    {
        private readonly ICurrentTenantService _currentTenantService;

        public ApplicationDbContext(
            DbContextOptions<ApplicationDbContext> options,
            ICurrentTenantService currentTenantService)
            : base(options)
        {
            // The tenant context is mandatory: a context with no notion of "who is
            // asking" must never be constructed, or isolation could silently fail.
            _currentTenantService = currentTenantService
                ?? throw new ArgumentNullException(nameof(currentTenantService));
        }

        // ── DbSets (representative subset; ~30 more omitted) ───────────────────
        public DbSet<Tenant> Tenants { get; set; }
        public DbSet<Branch> Branches { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<Product> Products { get; set; }
        public DbSet<Inventory> Inventories { get; set; }
        public DbSet<Sale> Sales { get; set; }
        public DbSet<SaleItem> SaleItems { get; set; }
        public DbSet<SalePayment> SalePayments { get; set; }
        public DbSet<Customer> Customers { get; set; }
        // … ~30 more DbSets omitted (inventory, tax, sync, register, vendor planes)

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 1. The isolation backbone — applied before anything else.
            ApplyGlobalQueryFilters(modelBuilder);

            // 2. Per-entity relationship / constraint configuration.
            //    ~50 ConfigureXxx(modelBuilder) calls omitted from this excerpt.
            //    ConfigureSale(modelBuilder);
            //    ConfigureProduct(modelBuilder);
            //    …

            // 3. Optimistic concurrency via PostgreSQL xmin system column.
            ConfigureConcurrencyTokens(modelBuilder);
        }

        /// <summary>
        /// Applies global query filters to automatically filter data by tenant.
        /// CRITICAL: this is what makes the system safe to run as multi-tenant SaaS.
        /// Every tenant-scoped entity gets the SAME predicate, so isolation is a
        /// property of the schema, not a discipline the query author must remember.
        /// The Tenant entity itself is intentionally excluded (it IS the tenant
        /// definition). Filters are bypassed ONLY when IsSuperAdmin is true — the
        /// single, audited path used by the vendor control plane.
        /// </summary>
        private void ApplyGlobalQueryFilters(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Branch>()
                .HasQueryFilter(e => _currentTenantService.IsSuperAdmin || e.TenantId == _currentTenantService.TenantId);

            modelBuilder.Entity<User>()
                .HasQueryFilter(e => _currentTenantService.IsSuperAdmin || e.TenantId == _currentTenantService.TenantId);

            modelBuilder.Entity<Product>()
                .HasQueryFilter(e => _currentTenantService.IsSuperAdmin || e.TenantId == _currentTenantService.TenantId);

            modelBuilder.Entity<Inventory>()
                .HasQueryFilter(e => _currentTenantService.IsSuperAdmin || e.TenantId == _currentTenantService.TenantId);

            modelBuilder.Entity<Sale>()
                .HasQueryFilter(e => _currentTenantService.IsSuperAdmin || e.TenantId == _currentTenantService.TenantId);

            modelBuilder.Entity<SaleItem>()
                .HasQueryFilter(e => _currentTenantService.IsSuperAdmin || e.TenantId == _currentTenantService.TenantId);

            modelBuilder.Entity<SalePayment>()
                .HasQueryFilter(e => _currentTenantService.IsSuperAdmin || e.TenantId == _currentTenantService.TenantId);

            modelBuilder.Entity<Customer>()
                .HasQueryFilter(e => _currentTenantService.IsSuperAdmin || e.TenantId == _currentTenantService.TenantId);

            // … the identical predicate is applied to every remaining tenant-scoped
            //   entity (~25 more): inventory transactions, refunds, expenses, audit
            //   logs, sync logs, tax profiles, register sessions, cash transactions, …
            //   Vendor control-plane entities are deliberately NOT tenant-scoped.
        }

        /// <summary>
        /// Registers PostgreSQL's built-in `xmin` system column as an optimistic
        /// concurrency token for every auditable entity. xmin changes on every row
        /// update, so a stale write is rejected automatically — with NO extra column
        /// and NO migration, because xmin already exists on every PostgreSQL row.
        /// Skipped on SQLite (the edge POS store), which has no xmin.
        /// </summary>
        private void ConfigureConcurrencyTokens(ModelBuilder modelBuilder)
        {
            if (!Database.IsNpgsql())
                return;

#pragma warning disable CS0618 // UseXminAsConcurrencyToken is obsolete in Npgsql 8.x but still functional; the suggested IsRowVersion() would require a migration to add a real column, which is unnecessary since xmin is a built-in PostgreSQL system column.
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                if (typeof(AuditableEntity).IsAssignableFrom(entityType.ClrType) && !entityType.ClrType.IsAbstract)
                {
                    modelBuilder.Entity(entityType.ClrType).UseXminAsConcurrencyToken();
                }
            }
#pragma warning restore CS0618
        }

        public override int SaveChanges()
        {
            UpdateAuditFields();
            return base.SaveChanges();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            UpdateAuditFields();
            return base.SaveChangesAsync(cancellationToken);
        }

        /// <summary>
        /// Automatically sets audit timestamps and — critically — stamps the current
        /// TenantId onto newly-inserted tenant entities. Because this runs inside
        /// SaveChanges, application code can never "forget" to set the tenant: an
        /// insert is automatically bound to whoever is asking.
        /// </summary>
        private void UpdateAuditFields()
        {
            var entries = ChangeTracker.Entries()
                .Where(e => e.Entity is BaseEntity &&
                            (e.State == EntityState.Added || e.State == EntityState.Modified));

            foreach (var entry in entries)
            {
                var entity = (BaseEntity)entry.Entity;

                if (entry.State == EntityState.Added)
                {
                    if (entity is AuditableEntity added)
                        added.CreatedDate = DateTime.UtcNow;

                    // Bind the new row to the ambient tenant.
                    if (entity is TenantEntity tenantEntity && _currentTenantService.TenantId.HasValue)
                        tenantEntity.TenantId = _currentTenantService.TenantId.Value;
                }
                else if (entry.State == EntityState.Modified)
                {
                    if (entity is AuditableEntity modified)
                        modified.UpdatedDate = DateTime.UtcNow;
                }
            }
        }
    }
}
