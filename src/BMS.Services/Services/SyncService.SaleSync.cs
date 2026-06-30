// ─────────────────────────────────────────────────────────────────────────────
//  PUBLIC SHOWCASE — CURATED EXCERPT (not the full production file)
//
//  This is the heart of the offline-first sync engine: the server-side routine
//  that ingests one sale pushed up from an edge POS terminal and reconciles it
//  into the authoritative cloud database.
//
//  The full SyncService is ~1,500 lines and also covers batch orchestration,
//  catalog pull, standalone payment sync, and sync-health dashboards. Those are
//  elided here so the single most important method — ProcessSingleSaleAsync —
//  can be read end to end.
//
//  The design principle on display is "Cloud Authoritative" reconciliation: a
//  sale that physically happened at the till MUST settle. The cloud never drops a
//  completed transaction — instead it deduplicates, verifies integrity, applies
//  the writes atomically, and records anomalies (negative stock, over-dispense,
//  bypassed prescription gate) for human oversight rather than rejecting reality.
//
//  Pipeline, in order:
//    0.  FluentValidation of the inbound DTO
//    0b. ZERO-TRUST register/branch verification (defeats cross-branch injection
//        even if the POS payload is tampered with)
//    1.  Idempotency: the sale GUID is the authoritative identity — a re-push of an
//        already-synced GUID is a silent no-op success, never an error
//    1b. SaleNumber-collision guard: a reused business number under a DIFFERENT
//        GUID is a terminal conflict (dead-lettered), not a retryable fault
//    2.  SHA-256 integrity check against the canonical hash shared with the edge
//    3-8 Atomic transaction: deserialize → FK-validate → insert sale/items/payments
//        → deduct inventory (negative allowed offline) → prescription-override audit
//        → cloud-authoritative batch-lot deduction → commit, with a race guard on
//        the unique-index INSERT.
// ─────────────────────────────────────────────────────────────────────────────

using System.Text.Json;
using AutoMapper;
using BMS.Shared.Utilities;
using BMS.Core.Entities;
using BMS.Core.Enums;
using BMS.Core.Interfaces;
using BMS.Data.Services.Interfaces;
using BMS.Services.Helpers;
using BMS.Services.Interfaces;
using BMS.Services.Utilities;
using BMS.Shared.Constants;
using BMS.Shared.DTOs.Common;
using BMS.Shared.DTOs.Sale;
using BMS.Shared.DTOs.Sync;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BMS.Services.Services;

public class SyncService : ISyncService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<SyncService> _logger;
    private readonly IMapper _mapper;
    private readonly IValidator<SyncSaleRequestDto> _saleValidator;
    private readonly IValidator<SyncStandalonePaymentDto> _paymentValidator;
    private readonly ICreditService _creditService;
    private readonly ICurrentTenantService _currentTenantService;

    public SyncService(
        IUnitOfWork unitOfWork,
        ILogger<SyncService> logger,
        IMapper mapper,
        IValidator<SyncSaleRequestDto> saleValidator,
        IValidator<SyncStandalonePaymentDto> paymentValidator,
        ICreditService creditService,
        ICurrentTenantService currentTenantService)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _mapper = mapper;
        _saleValidator = saleValidator;
        _paymentValidator = paymentValidator;
        _creditService = creditService;
        _currentTenantService = currentTenantService;
    }

    // NOTE: Batch orchestration (ProcessSalesBatchAsync), catalog pull
    // (GetCatalogAsync), standalone payment sync, rejection-log persistence
    // (PersistRejectionLogAsync), and the sync-health surfaces are omitted from
    // this excerpt. The single sale-ingestion path below is the representative core.

    private async Task<SyncResultDto> ProcessSingleSaleAsync(SyncSaleRequestDto request, Guid? authenticatedBranchId, CancellationToken ct)
    {
        try
        {
            // 0. Validation
            var validationResult = await _saleValidator.ValidateAsync(request, ct);
            if (!validationResult.IsValid)
            {
                var validationMsg = string.Join("; ", validationResult.Errors.Select(e => e.ErrorMessage));
                // Persist cloud-side rejection log so the Sync Health dashboard gains visibility.
                // BranchId from DTO is unverified at this stage but is the best available anchor.
                if (request.BranchId != Guid.Empty)
                    await PersistRejectionLogAsync(request, request.BranchId, "ValidationError", validationMsg, ct);
                return new SyncResultDto
                {
                    Id = request.Id,
                    ReceiptNumber = request.ReceiptNumber,
                    Status = SyncStatus.ValidationError,
                    Message = validationMsg
                };
            }

            // 0b. ZERO TRUST — Verify RegisterId maps to a real server-side Register and that
            //     its LocationId matches the BranchId embedded in the DTO.
            //     This prevents cross-branch injection even if the POS DTO is manipulated.
            var register = await _unitOfWork.Registers.GetSingleOrDefaultAsync(r => r.Id == request.RegisterId, ct);
            if (register == null)
            {
                _logger.LogWarning("Zero Trust: Register {RegisterId} not found for receipt {Receipt}.",
                    request.RegisterId, request.ReceiptNumber);
                var registerNotFoundMsg = $"Register {request.RegisterId} not found.";
                if (request.BranchId != Guid.Empty)
                    await PersistRejectionLogAsync(request, request.BranchId, "ValidationError", registerNotFoundMsg, ct);
                return new SyncResultDto
                {
                    Id = request.Id,
                    ReceiptNumber = request.ReceiptNumber,
                    Status = SyncStatus.ValidationError,
                    Message = registerNotFoundMsg
                };
            }

            if (register.LocationId != request.BranchId)
            {
                _logger.LogWarning(
                    "Zero Trust: Register branch mismatch for receipt {Receipt}. Register.LocationId={LocationId}, DTO.BranchId={DtoBranchId}.",
                    request.ReceiptNumber, register.LocationId, request.BranchId);
                await PersistRejectionLogAsync(request, register.LocationId, "BranchMismatch",
                    "Register branch mismatch. Terminal is not authorized for this branch.", ct);
                return new SyncResultDto
                {
                    Id = request.Id,
                    ReceiptNumber = request.ReceiptNumber,
                    Status = SyncStatus.ValidationError,
                    Message = "Register branch mismatch. Terminal is not authorized for this branch."
                };
            }

            if (authenticatedBranchId.HasValue && request.BranchId != authenticatedBranchId.Value)
            {
                _logger.LogWarning(
                    "Zero Trust: JWT claim branch mismatch for receipt {Receipt}. JWT.BranchId={JwtBranchId}, DTO.BranchId={DtoBranchId}.",
                    request.ReceiptNumber, authenticatedBranchId.Value, request.BranchId);
                await PersistRejectionLogAsync(request, register.LocationId, "BranchMismatch",
                    "Authenticated user's branch does not match sale branch.", ct);
                return new SyncResultDto
                {
                    Id = request.Id,
                    ReceiptNumber = request.ReceiptNumber,
                    Status = SyncStatus.ValidationError,
                    Message = "Authenticated user's branch does not match sale branch."
                };
            }

            // 1. Idempotency Check (BEFORE transaction — read-only, avoids unnecessary locks).
            // The sale GUID is the AUTHORITATIVE sync identity. A re-push of an already-synced
            // sale (same GUID) is a no-op success — NEVER an error.
            var existingSale = await _unitOfWork.Sales.GetSingleOrDefaultAsync(s => s.Id == request.Id, ct);
            if (existingSale != null)
            {
                // Diagnostics: distinguish "skipped (already-synced)" from "inserted fresh"
                // (logged at the success path) so a re-push storm is explainable from the server log.
                _logger.LogInformation(
                    "Sync SKIP (idempotent): sale {SaleId} (receipt {Receipt}) already present on the server — returning AlreadySynced, no insert.",
                    request.Id, request.ReceiptNumber);
                return new SyncResultDto
                {
                    Id = request.Id,
                    ReceiptNumber = request.ReceiptNumber,
                    Status = SyncStatus.AlreadySynced,
                    Message = "Record already exists."
                };
            }

            // 1b. SaleNumber-collision guard.
            // SaleNumber is a BUSINESS/display key under the unique index IX_Sales_TenantId_SaleNumber —
            // it must NOT be the sync conflict discriminant. A new GUID that reuses a SaleNumber already
            // held by a DIFFERENT sale is a genuine cross-register numbering conflict (e.g. an edge DB
            // was reset and reissued numbers, or two registers issued the same number) — a configuration
            // error, NOT a retryable server fault. Detect it proactively and dead-letter it (terminal
            // ValidationError → the edge MarkSuspended path) rather than letting the blind INSERT throw
            // a unique-violation inside the transaction and bounce forever as a retryable ServerError.
            if (!string.IsNullOrWhiteSpace(request.ReceiptNumber))
            {
                var numberClash = await _unitOfWork.Sales.GetSingleOrDefaultAsync(
                    s => s.SaleNumber == request.ReceiptNumber && s.Id != request.Id, ct);
                if (numberClash != null)
                {
                    _logger.LogError(
                        "Sync CONFLICT: SaleNumber {SaleNumber} already belongs to sale {ExistingId}; incoming sale {IncomingId} is a DIFFERENT record. " +
                        "Genuine cross-register numbering conflict — dead-lettering for manual intervention (NOT retried).",
                        request.ReceiptNumber, numberClash.Id, request.Id);
                    await PersistRejectionLogAsync(request, register.LocationId, "DuplicateSaleNumber",
                        $"SaleNumber '{request.ReceiptNumber}' is already assigned to a different sale ({numberClash.Id}). " +
                        "Two registers issued the same number, or an edge DB reset reissued numbers the server already holds.", ct);
                    return new SyncResultDto
                    {
                        Id = request.Id,
                        ReceiptNumber = request.ReceiptNumber,
                        Status = SyncStatus.ValidationError,
                        Message = $"Duplicate SaleNumber '{request.ReceiptNumber}' — already assigned to a different sale on the server. " +
                                  "Genuine cross-register numbering conflict; manual intervention required (not retried)."
                    };
                }
            }

            // 2. Hash Verification (Tamper Check)
            // SaleIntegrityUtility is the single canonical formula shared by Register + API.
            // SaleDataJson is transmitted byte-for-byte, so including it guarantees
            // any item-level tampering (Qty, Price, TaxBadge, VariantId) is caught.
            var isSplit = request.PaymentMethod == PaymentMethod.Split || request.HasSplitPayment;
            var hashInput = SaleIntegrityUtility.BuildHashInput(
                request.TotalAmount, request.CreatedAt, request.RegisterId,
                request.LocalSequence, request.SaleDataJson, isSplit);

            _logger.LogDebug("SERVER HASH INPUT: {HashInput}", hashInput);
            var computedHash = SaleIntegrityUtility.ComputeHash(hashInput);

            if (computedHash != request.ConflictHash)
            {
                _logger.LogWarning("Hash Mismatch for Receipt {Receipt}. Client: {ClientHash}, Server: {ServerHash}",
                    request.ReceiptNumber, request.ConflictHash, computedHash);
                await PersistRejectionLogAsync(request, register.LocationId, "HashMismatch",
                    $"Integrity check failed. Client: {request.ConflictHash}, Server: {computedHash}", ct);
                return new SyncResultDto
                {
                    Id = request.Id,
                    ReceiptNumber = request.ReceiptNumber,
                    Status = SyncStatus.HashMismatch,
                    Message = "Integrity check failed. Conflict Hash mismatch."
                };
            }

            // === BEGIN TRANSACTION ===
            // All writes (Sale + SaleItems + Payments + Inventory) are atomic.
            await _unitOfWork.BeginTransactionAsync(ct);

            try
            {
                // 3. Deserialize SaleDataJson to extract line items for SaleItem creation + inventory deduction
                List<SaleItemSnapshotDto> saleItems = new();
                if (!string.IsNullOrWhiteSpace(request.SaleDataJson))
                {
                    var snapshot = JsonSerializer.Deserialize<SaleDataSnapshotDto>(request.SaleDataJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (snapshot?.Items != null)
                    {
                        saleItems = snapshot.Items;
                    }
                }

                // 4. Validate foreign keys before INSERT
                // The POS may reference a local-only customer (e.g. Walk-in) that doesn't exist
                // in the cloud database. Null it out so the sale still syncs.
                Guid? validatedCustomerId = null;
                if (request.CustomerId.HasValue && request.CustomerId.Value != Guid.Empty)
                {
                    var customerExists = await _unitOfWork.Repository<Customer>()
                        .GetByIdAsync(request.CustomerId.Value, ct);
                    if (customerExists != null)
                    {
                        validatedCustomerId = request.CustomerId.Value;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "CustomerId {CustomerId} from POS does not exist in cloud DB. Setting to null for sale {Receipt}.",
                            request.CustomerId.Value, request.ReceiptNumber);
                    }
                }

                // 5. Create Sale entity
                var saleEntity = new Sale
                {
                    Id = request.Id,
                    SaleNumber = request.ReceiptNumber,
                    ReceiptNumber = request.ReceiptNumber,
                    BranchId = request.BranchId,
                    CashierId = request.CashierId,
                    RegisterId = request.RegisterId != Guid.Empty ? request.RegisterId : null,
                    TotalAmount = request.TotalAmount,
                    SaleDate = DateTime.SpecifyKind(request.CreatedAt, DateTimeKind.Utc),
                    PaymentMethod = request.PaymentMethod,
                    CustomerId = validatedCustomerId,
                    LocalSequence = request.LocalSequence,
                    // Immutable temporal snapshots — the receipt must reproduce exactly as issued,
                    // even if the customer/branch/tax config changes later.
                    CustomerNameSnapshot = request.CustomerNameSnapshot,
                    BranchAddressSnapshot = request.BranchAddressSnapshot,
                    TaxBreakdownJson = request.TaxBreakdownJson,
                };

                // 5b. Create SaleItems from deserialized snapshot
                saleEntity.SaleItems ??= new List<SaleItem>();
                foreach (var item in saleItems)
                {
                    saleEntity.SaleItems.Add(new SaleItem
                    {
                        SaleId = request.Id,
                        ProductId = item.ProductId,
                        ProductName = item.Description,
                        Quantity = item.Quantity,
                        UnitPrice = item.UnitPrice,
                        LineTotal = item.Quantity * item.UnitPrice,
                        RefundedQuantity = 0,
                        VariantId = item.VariantId,
                        TaxBadge = item.TaxBadge,
                        BatchLotId = item.BatchLotId // carry dispensed batch for recall traceability
                    });
                }

                // 6. Map split payments (append-only ledger; idempotency key carried per payment)
                if (request.Payments != null && request.Payments.Any())
                {
                    foreach (var p in request.Payments)
                    {
                        saleEntity.SalePayments.Add(new SalePayment
                        {
                            Id = p.Id,
                            SaleId = request.Id,
                            PaymentMethod = p.PaymentMethod.ToString(),
                            Amount = p.Amount,
                            Tendered = p.Tendered,
                            Change = p.Change,
                            TransactionReference = p.Reference,
                            PaymentDate = DateTime.SpecifyKind(request.CreatedAt, DateTimeKind.Utc),
                            IdempotencyKey = p.IdempotencyKey,
                            PaymentSource = p.PaymentSource,
                            Notes = p.Notes,
                            ProcessingType = p.ProcessingType,
                            MobileNetwork = p.MobileNetwork,
                            MobileNumber = p.MobileNumber,
                            RequiresAudit = p.RequiresAudit
                        });
                    }
                }

                // Derive payment status + financial summary from the synced items and payments.
                var totalPaid = saleEntity.SalePayments.Sum(p => p.Amount);
                saleEntity.PaymentStatus = CreditCalculationHelper.ComputePaymentStatus(
                    saleEntity.TotalAmount, totalPaid);
                saleEntity.Subtotal    = saleEntity.SaleItems.Sum(i => i.UnitPrice * i.Quantity);
                saleEntity.AmountPaid  = saleEntity.SalePayments.Any() ? saleEntity.SalePayments.Sum(p => p.Tendered) : saleEntity.TotalAmount;
                saleEntity.ChangeGiven = saleEntity.SalePayments.Any() ? saleEntity.SalePayments.Sum(p => p.Change)   : 0m;

                await _unitOfWork.Sales.AddAsync(saleEntity, ct);

                // 7. INVENTORY DEDUCTION (CRITICAL)
                // Deduct QuantityInStock for each sale item at the sale's branch.
                // Allows negative stock (no constraint) — offline sales may exceed known stock,
                // and a sale that physically happened must never be rejected for it.
                foreach (var item in saleItems)
                {
                    // Deduct from the row that was actually sold: a variant line decrements that
                    // variant's own stock row; a non-variant line decrements the product's base pool.
                    var inventory = await _unitOfWork.Inventory
                        .GetByProductVariantAndBranchAsync(item.ProductId, item.VariantId, request.BranchId);

                    if (inventory != null)
                    {
                        // Stock contract is decimal(18,3) end-to-end (supports weighed / loose goods).
                        inventory.QuantityInStock -= item.Quantity;
                        if (inventory.QuantityInStock < 0)
                        {
                            _logger.LogWarning(
                                "Negative stock after sync deduction: ProductId={ProductId}, VariantId={VariantId}, BranchId={BranchId}, NewQty={Qty}.",
                                item.ProductId, item.VariantId, request.BranchId, inventory.QuantityInStock);
                        }
                    }
                    else
                    {
                        // No inventory record for this product(+variant)/branch — log but don't fail the sale.
                        // Can happen if the product was created after the last catalog sync.
                        _logger.LogWarning(
                            "No inventory record found for ProductId={ProductId}, VariantId={VariantId}, BranchId={BranchId} during sale sync. Skipping deduction.",
                            item.ProductId, item.VariantId, request.BranchId);
                    }
                }

                // 7b. Prescription-override audit trail for this edge-reconciled sale.
                // The Register already enforced the soft gate offline, so we NEVER reject an
                // already-completed sale here — we persist the override trail and flag any
                // uncovered Rx product as an anomaly for oversight (Cloud Authoritative
                // reconciliation must never drop a completed transaction).
                if (request.PrescriptionOverrides.Count > 0)
                {
                    // A manager id the cloud cannot resolve (e.g. an offline-only user not yet
                    // synced) is still recorded verbatim in the audit detail, but its FK is nulled
                    // so it can never FK-violate and roll back the completed sale.
                    var claimedAuthorizerIds = request.PrescriptionOverrides
                        .Where(o => o.AuthorizedByUserId != Guid.Empty)
                        .Select(o => o.AuthorizedByUserId)
                        .Distinct()
                        .ToList();

                    var validAuthorizerIds = claimedAuthorizerIds.Count == 0
                        ? new HashSet<Guid>()
                        : (await _unitOfWork.Users.GetQueryable()
                            .Where(u => claimedAuthorizerIds.Contains(u.Id))
                            .Select(u => u.Id)
                            .ToListAsync(ct)).ToHashSet();

                    foreach (var ovr in request.PrescriptionOverrides
                                 .GroupBy(o => o.ProductId)
                                 .Select(g => g.First()))
                    {
                        Guid? attributed = validAuthorizerIds.Contains(ovr.AuthorizedByUserId)
                            ? ovr.AuthorizedByUserId
                            : null;

                        var overrideAudit = PrescriptionOverrideAuditFactory.Build(
                            ovr,
                            saleId: saleEntity.Id,
                            saleNumber: saleEntity.SaleNumber ?? request.ReceiptNumber,
                            branchId: request.BranchId,
                            tenantId: saleEntity.TenantId,
                            attributedUserId: attributed,
                            originatedFrom: PrescriptionOverrideAuditFactory.OriginPos);
                        await _unitOfWork.Repository<AuditLog>().AddAsync(overrideAudit, ct);
                    }
                }

                // Anomaly visibility (non-blocking): a synced sale that contains a
                // prescription-only product but no matching override indicates the edge gate
                // was bypassed. Surface it in the logs; never reject the completed sale.
                var overriddenProductIds = request.PrescriptionOverrides
                    .Select(o => o.ProductId).ToHashSet();
                var saleProductIds = saleItems.Select(i => i.ProductId).Distinct().ToList();
                if (saleProductIds.Count > 0)
                {
                    var rxProductIds = await _unitOfWork.Products.GetQueryable()
                        .Where(p => saleProductIds.Contains(p.Id) && p.RequiresPrescription)
                        .Select(p => p.Id)
                        .ToListAsync(ct);
                    foreach (var rxId in rxProductIds.Where(id => !overriddenProductIds.Contains(id)))
                    {
                        _logger.LogWarning(
                            "Synced sale {Receipt} contains prescription-only product {ProductId} with no override record — edge gate may have been bypassed.",
                            request.ReceiptNumber, rxId);
                    }
                }

                // 7c. BATCH LOT DEDUCTION (Cloud-Authoritative).
                // For each line that recorded a dispensed lot, deduct the server BatchLot. A completed
                // offline batch sale is NEVER rejected here: an over-dispense is ALLOWED to drive the lot
                // negative (mirroring the negative-stock policy above) and recorded as a non-blocking
                // anomaly — we do NOT roll back or quarantine the sale. The till (CheckoutService) and the
                // online pre-commit (SalesService) still HARD-BLOCK over-dispense; only this
                // reconciliation path is permissive, because the transaction already physically happened.
                var batchLines = saleItems
                    .Where(i => i.BatchLotId.HasValue && i.BatchLotId.Value != Guid.Empty)
                    .ToList();
                foreach (var line in batchLines)
                {
                    var batch = await _unitOfWork.Repository<BatchLot>().GetByIdAsync(line.BatchLotId!.Value, ct);
                    if (batch == null)
                    {
                        // The lot reference is preserved on the SaleItem for recall traceability even when
                        // the server lot is unknown (e.g. created on a register that has not yet synced it).
                        _logger.LogWarning(
                            "Synced sale {Receipt} references batch lot {BatchLotId} not present on the server — skipping batch deduction (recall trace retained on the sale item).",
                            request.ReceiptNumber, line.BatchLotId);
                        continue;
                    }

                    bool over = BatchDeductionMath.IsOverDispense(batch.Quantity, line.Quantity);
                    decimal before = batch.Quantity;
                    var (newQty, newStatus) = BatchDeductionMath.ApplyDispense(batch.Quantity, (int)batch.Status, line.Quantity);
                    batch.Quantity = newQty;
                    batch.Status = (BatchStatus)newStatus;
                    _unitOfWork.Repository<BatchLot>().Update(batch);

                    if (over)
                    {
                        _logger.LogWarning(
                            "Over-dispense on sync: sale {Receipt} dispensed {Qty} from batch {BatchNumber} ({BatchLotId}) holding {Before}, leaving {After} (negative). Deducted per Cloud-Authoritative policy; recorded as anomaly — sale NOT rejected.",
                            request.ReceiptNumber, line.Quantity, batch.BatchNumber, batch.Id, before, newQty);

                        var anomaly = BatchOverDispenseAuditFactory.Build(
                            batch,
                            saleId: saleEntity.Id,
                            saleNumber: saleEntity.SaleNumber ?? request.ReceiptNumber,
                            quantityDispensed: line.Quantity,
                            quantityBefore: before,
                            quantityAfter: newQty,
                            branchId: request.BranchId,
                            tenantId: saleEntity.TenantId);
                        await _unitOfWork.Repository<AuditLog>().AddAsync(anomaly, ct);
                    }
                }

                // 8. Commit all writes atomically
                await _unitOfWork.SaveChangesAsync(ct);
                await _unitOfWork.CommitTransactionAsync(ct);

                _logger.LogInformation("Synced Sale {Receipt} successfully ({ItemCount} items, inventory deducted).",
                    request.ReceiptNumber, saleItems.Count);

                return new SyncResultDto
                {
                    Id = request.Id,
                    ReceiptNumber = request.ReceiptNumber,
                    Status = SyncStatus.Success
                };
            }
            catch (DbUpdateException dbEx) when (
                DbExceptionHelper.IsUniqueConstraintViolation(dbEx, out var clashedConstraint)
                && clashedConstraint == DbConstraints.SaleNumber)
            {
                // Race guard for the proactive 1b check: if two sales carrying the same SaleNumber
                // but different GUIDs arrive concurrently, one wins the INSERT and the loser trips the
                // unique index here. Classify it as the SAME terminal numbering conflict (NOT a
                // retryable ServerError) so it dead-letters instead of looping forever.
                await _unitOfWork.RollbackTransactionAsync(ct);
                _logger.LogError(dbEx,
                    "Sync CONFLICT (race): INSERT of sale {IncomingId} tripped {Constraint} for SaleNumber {SaleNumber}. " +
                    "Dead-lettering for manual intervention (NOT retried).",
                    request.Id, clashedConstraint, request.ReceiptNumber);
                await PersistRejectionLogAsync(request, request.BranchId, "DuplicateSaleNumber",
                    $"SaleNumber '{request.ReceiptNumber}' collided on INSERT ({clashedConstraint}).", ct);
                return new SyncResultDto
                {
                    Id = request.Id,
                    ReceiptNumber = request.ReceiptNumber,
                    Status = SyncStatus.ValidationError,
                    Message = $"Duplicate SaleNumber '{request.ReceiptNumber}' — collided with an existing sale on insert. " +
                              "Genuine cross-register numbering conflict; manual intervention required (not retried)."
                };
            }
            catch (Exception)
            {
                // Rollback the entire transaction on any failure
                await _unitOfWork.RollbackTransactionAsync(ct);
                throw; // Re-throw to be caught by outer handler
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing sale {Receipt}", request.ReceiptNumber);
            return new SyncResultDto
            {
                Id = request.Id,
                ReceiptNumber = request.ReceiptNumber,
                Status = SyncStatus.ServerError,
                Message = ex.Message
            };
        }
    }
}
