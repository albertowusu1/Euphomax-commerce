using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BMS.Core.Entities.Base;
using BMS.Core.Enums;

namespace BMS.Core.Entities
{
    /// <summary>
    /// Represents a single payment event against a Sale.
    /// Forms an append-only ledger — never update or delete payment records.
    /// Financial state (TotalPaid, BalanceDue, PaymentStatus) is derived from these records.
    /// </summary>
    public class SalePayment : TenantEntity
    {
        [Required]
        public Guid SaleId { get; set; }

        [ForeignKey(nameof(SaleId))]
        public virtual Sale? Sale { get; set; }

        /// <summary>
        /// Payment method: "Cash", "Card", "MobileMoney", "BankTransfer", etc.
        /// </summary>
        [Required]
        [MaxLength(20)]
        public string PaymentMethod { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        /// <summary>
        /// External transaction reference (e.g., MoMo Transaction ID).
        /// </summary>
        [MaxLength(100)]
        public string? TransactionReference { get; set; }

        /// <summary>
        /// Processing flow used (Automated vs Manual).
        /// Default to Automated for backward compatibility.
        /// </summary>
        public PaymentProcessingType ProcessingType { get; set; } = PaymentProcessingType.AutomatedGateway;

        /// <summary>
        /// Specific network for MoMo payments (MTN, Telecel, AT).
        /// </summary>
        [MaxLength(20)]
        public string? MobileNetwork { get; set; }

        /// <summary>
        /// Customer phone number for MoMo payments.
        /// </summary>
        [MaxLength(20)]
        public string? MobileNumber { get; set; }

        /// <summary>
        /// Flag for payments requiring manual verification (e.g., duplicate references).
        /// </summary>
        public bool RequiresAudit { get; set; } = false;

        /// <summary>
        /// Timestamp when manual payment was reconciled by admin.
        /// </summary>
        public DateTime? ReconciledAt { get; set; }

        /// <summary>
        /// When this payment event occurred.
        /// Uses DateTimeOffset to prevent timezone bugs between Ghana GMT (local POS)
        /// and Azure UTC (cloud) — critical for Z-Report accuracy.
        /// </summary>
        public DateTimeOffset PaymentDate { get; set; } = DateTimeOffset.UtcNow;

        // ── Micro-Credit / Ledger Fields ────────────────────────────────────────

        /// <summary>
        /// Unique key for idempotent payment insertion during sync retries.
        /// Format: {RegisterId}:{RegisterSessionId}:{NewGuid}
        /// Prevents duplicate payment records when the same event is synced multiple times.
        /// </summary>
        [MaxLength(64)]
        public string? IdempotencyKey { get; set; }

        /// <summary>
        /// Identifies which system recorded this payment (POS, Portal, Public).
        /// </summary>
        public PaymentSource PaymentSource { get; set; } = PaymentSource.POS;

        /// <summary>
        /// Register that recorded this payment. Null for Portal-originated payments.
        /// </summary>
        public Guid? RegisterId { get; set; }

        /// <summary>
        /// Cashier session during which this payment was recorded. Null for Portal-originated payments.
        /// </summary>
        public Guid? RegisterSessionId { get; set; }

        /// <summary>
        /// Optional note (e.g., "Partial payment", "Debt collection").
        /// </summary>
        [MaxLength(200)]
        public string? Notes { get; set; }

        /// <summary>
        /// Amount physically tendered by the customer for this payment method.
        /// May exceed Amount when change is given (cash only).
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal Tendered { get; set; }

        /// <summary>
        /// Change returned to the customer for this payment method.
        /// Tendered - Amount = Change (for cash payments).
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal Change { get; set; }
    }
}
