using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BMS.Core.Entities;
using BMS.Core.Enums;
using BMS.IntegrationTests.Infrastructure;
using BMS.Shared.DTOs.Sale;
using BMS.Shared.DTOs.Sync;
using BMS.Shared.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BMS.IntegrationTests.Tests;

/// <summary>
/// Sync sales-push idempotency, proven against the PostgreSQL test
/// container. The sale GUID is the authoritative sync identity; SaleNumber is a business/display key
/// under the unique index <c>IX_Sales_TenantId_SaleNumber</c> and must NOT be the conflict discriminant.
///
///   AC#1  Re-pushing a sale whose GUID already exists succeeds silently (AlreadySynced), no duplicate row.
///   AC#2  A sale whose SaleNumber exists under a DIFFERENT GUID fails loudly + terminally
///         (ValidationError → edge dead-letter), never a retryable ServerError and never a raw 23505.
///   AC#4  Existing sync behaviour is unaffected; idempotent re-push is regression-locked here.
/// </summary>
[Collection("IntegrationDB")]
public sealed class SyncIdempotencyTests : IntegrationTestBase
{
    private readonly IntegrationTestFactory _factory;
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public SyncIdempotencyTests(IntegrationTestFactory factory) : base(factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        var token = TestJwtHelper.GenerateAdminToken(
            userId: IntegrationTestSeeder.AdminUserId,
            tenantId: IntegrationTestSeeder.TenantId,
            branchId: IntegrationTestSeeder.BranchId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    // =========================================================================
    // AC#1 / AC#4 — idempotent re-push of the SAME sale (same GUID) is a no-op success
    // =========================================================================

    [Fact]
    public async Task RePushingSameSaleGuid_SucceedsSilently_NoDuplicateRow()
    {
        var productId = Guid.NewGuid();
        var registerId = Guid.NewGuid();
        SeedProductWithStock(productId, $"IDEM-OK-{Guid.NewGuid():N}".Substring(0, 16));
        SeedRegister(registerId);

        var saleId = Guid.NewGuid();
        var receipt = $"IDEM-{Guid.NewGuid():N}".Substring(0, 16);
        var sale = BuildSyncSale(saleId, registerId, productId, receipt,
            qty: 2m, unitPrice: 4.00m, createdAt: new DateTime(2026, 6, 21, 9, 0, 0, DateTimeKind.Utc), localSequence: 1);

        // First push — inserted fresh.
        var first = await _client.PostAsJsonAsync("/api/sync/sales", new BatchSyncRequestDto { RegisterId = registerId, Sales = [sale] });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStatusAsync(first, saleId)).Should().Be(201, "the first push inserts the sale fresh (Success)");

        // Second push — byte-for-byte identical (same GUID). Must be idempotent.
        var second = await _client.PostAsJsonAsync("/api/sync/sales", new BatchSyncRequestDto { RegisterId = registerId, Sales = [sale] });
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStatusAsync(second, saleId)).Should().Be(204, "a re-push of an already-synced GUID returns AlreadySynced, never an error");

        using var scope = _factory.CreateSuperAdminDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BMS.Data.ApplicationDbContext>();
        (await db.Sales.AsNoTracking().CountAsync(s => s.Id == saleId))
            .Should().Be(1, "the idempotent re-push must NOT create a second row");
    }

    // =========================================================================
    // AC#2 — same SaleNumber, DIFFERENT GUID is a genuine conflict: terminal, not retryable, no 23505
    // =========================================================================

    [Fact]
    public async Task PushingDuplicateSaleNumberUnderDifferentGuid_FailsTerminally_NoDuplicateKeyError()
    {
        var productId = Guid.NewGuid();
        var registerId = Guid.NewGuid();
        SeedProductWithStock(productId, $"IDEM-CONF-{Guid.NewGuid():N}".Substring(0, 16));
        SeedRegister(registerId);

        // A sale already holds receipt number "CLASH-..." under its OWN GUID (e.g. synced before an edge reset).
        var receipt = $"CLASH-{Guid.NewGuid():N}".Substring(0, 16);
        var incumbentId = Guid.NewGuid();
        SeedExistingSale(incumbentId, receipt);

        // The edge re-issues the SAME number to a DIFFERENT sale (new GUID) after a DB reset.
        var intruderId = Guid.NewGuid();
        var intruder = BuildSyncSale(intruderId, registerId, productId, receipt,
            qty: 1m, unitPrice: 4.00m, createdAt: new DateTime(2026, 6, 21, 10, 0, 0, DateTimeKind.Utc), localSequence: 1);

        var response = await _client.PostAsJsonAsync("/api/sync/sales", new BatchSyncRequestDto { RegisterId = registerId, Sales = [intruder] });
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the batch envelope still returns 200; the per-sale result carries the failure");

        var (status, message) = await ReadResultAsync(response, intruderId);
        status.Should().Be(400, "a genuine cross-register numbering conflict is terminal (ValidationError → dead-letter), NOT a retryable ServerError (500)");
        message.Should().Contain("Duplicate SaleNumber", "the failure must be loud and self-explanatory for manual intervention");

        using var scope = _factory.CreateSuperAdminDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BMS.Data.ApplicationDbContext>();
        (await db.Sales.AsNoTracking().AnyAsync(s => s.Id == intruderId))
            .Should().BeFalse("the conflicting sale must NOT be inserted");
        (await db.Sales.AsNoTracking().CountAsync(s => s.SaleNumber == receipt))
            .Should().Be(1, "only the incumbent sale retains the number");
        (await db.SyncRejectionLogs.AsNoTracking().AnyAsync(r => r.LocalSaleId == intruderId && r.RejectionReason == "DuplicateSaleNumber"))
            .Should().BeTrue("the conflict must be recorded in the rejection log for oversight");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static SyncSaleRequestDto BuildSyncSale(
        Guid saleId, Guid registerId, Guid productId, string receiptNumber,
        decimal qty, decimal unitPrice, DateTime createdAt, long localSequence)
    {
        var total = qty * unitPrice;
        var snapshot = new SaleDataSnapshotDto
        {
            PaymentMethod = "Cash",
            Tendered = total,
            Change = 0m,
            Items =
            [
                new SaleItemSnapshotDto
                {
                    ProductId = productId,
                    Description = "Idempotency Probe",
                    Quantity = qty,
                    UnitPrice = unitPrice,
                    TaxBadge = string.Empty
                }
            ]
        };
        var saleDataJson = JsonSerializer.Serialize(snapshot, _json);
        var hashInput = SaleIntegrityUtility.BuildHashInput(total, createdAt, registerId, localSequence, saleDataJson, isSplit: false);

        return new SyncSaleRequestDto
        {
            Id = saleId,
            ReceiptNumber = receiptNumber,
            RegisterId = registerId,
            BranchId = IntegrationTestSeeder.BranchId,
            CashierId = IntegrationTestSeeder.AdminUserId,
            CreatedAt = createdAt,
            TotalAmount = total,
            PaymentMethod = PaymentMethod.Cash,
            PaymentStatus = 2, // Paid
            SaleDataJson = saleDataJson,
            ConflictHash = SaleIntegrityUtility.ComputeHash(hashInput),
            LocalSequence = localSequence
        };
    }

    private static async Task<int> ReadStatusAsync(HttpResponseMessage response, Guid saleId)
        => (await ReadResultAsync(response, saleId)).Status;

    private static async Task<(int Status, string? Message)> ReadResultAsync(HttpResponseMessage response, Guid saleId)
    {
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("data").GetProperty("results");
        var result = results.EnumerateArray().First(r => Guid.Parse(r.GetProperty("id").GetString()!) == saleId);
        var statusEl = result.GetProperty("status");
        var status = statusEl.ValueKind == JsonValueKind.Number
            ? statusEl.GetInt32()
            : (int)Enum.Parse<SyncStatus>(statusEl.GetString()!);
        var message = result.TryGetProperty("message", out var m) ? m.GetString() : null;
        return (status, message);
    }

    private void SeedProductWithStock(Guid productId, string code)
    {
        using var scope = _factory.CreateSuperAdminDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BMS.Data.ApplicationDbContext>();
        db.Products.Add(new Product
        {
            Id = productId,
            TenantId = IntegrationTestSeeder.TenantId,
            ProductCode = code,
            ProductName = "Idempotency Probe",
            Barcode = code,
            CategoryId = IntegrationTestSeeder.CategoryId,
            SupplierId = IntegrationTestSeeder.SupplierId,
            CostPrice = 2.00m,
            SellingPrice = 4.00m,
            Unit = "each",
            ReorderLevel = 5,
            TrackInventory = true,
            IsActive = true
        });
        db.Inventories.Add(new Inventory
        {
            TenantId = IntegrationTestSeeder.TenantId,
            ProductId = productId,
            BranchId = IntegrationTestSeeder.BranchId,
            QuantityInStock = 100m,
            ReorderLevel = 5,
            UnitCost = 2.00m
        });
        db.SaveChanges();
    }

    private void SeedRegister(Guid registerId)
    {
        using var scope = _factory.CreateSuperAdminDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BMS.Data.ApplicationDbContext>();
        db.Registers.Add(new Register
        {
            Id = registerId,
            TenantId = IntegrationTestSeeder.TenantId,
            RegisterCode = "IDR" + registerId.ToString("N")[..3].ToUpperInvariant(),
            RegisterName = "Idempotency Test Register",
            LocationId = IntegrationTestSeeder.BranchId, // zero-trust: must equal the sale BranchId
            IsActive = true
        });
        db.SaveChanges();
    }

    private void SeedExistingSale(Guid saleId, string saleNumber)
    {
        using var scope = _factory.CreateSuperAdminDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BMS.Data.ApplicationDbContext>();
        db.Sales.Add(new Sale
        {
            Id = saleId,
            TenantId = IntegrationTestSeeder.TenantId,
            SaleNumber = saleNumber,
            ReceiptNumber = saleNumber,
            BranchId = IntegrationTestSeeder.BranchId,
            CashierId = IntegrationTestSeeder.AdminUserId,
            Status = "Completed",
            PaymentMethod = PaymentMethod.Cash,
            PaymentStatus = PaymentStatus.Paid,
            Subtotal = 4.00m,
            TotalAmount = 4.00m,
            AmountPaid = 4.00m,
            SaleDate = DateTime.UtcNow
        });
        db.SaveChanges();
    }
}
