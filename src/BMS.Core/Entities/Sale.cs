using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using BMS.Core.Entities.Base;
using BMS.Core.Enums;

namespace BMS.Core.Entities
{
    /// <summary>
    /// Represents a completed sales transaction.
    /// </summary>
    public class Sale : TenantEntity
    {
        #region Transaction Info

        /// <summary>
        /// Human-readable sale number.
        /// Format: INV-2024-00001
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string SaleNumber { get; set; }

        [Required]
        public Guid BranchId { get; set; }

        [ForeignKey(nameof(BranchId))]
        public virtual Branch? Branch { get; set; }

        /// <summary>
        /// Register (POS terminal) that processed this sale.
        /// </summary>
        public Guid? RegisterId { get; set; }

        [ForeignKey(nameof(RegisterId))]
        public virtual Register? Register { get; set; }

        /// <summary>
        /// Register session (cashier shift) during which this sale was made.
        /// </summary>
        public Guid? RegisterSessionId { get; set; }

        [ForeignKey(nameof(RegisterSessionId))]
        public virtual RegisterSession? RegisterSession { get; set; }

        /// <summary>
        /// Receipt number in format: {LocationPrefix}-{RegisterCode}-{SequenceNumber}
        /// Example: ACC-R1-000047
        /// </summary>
        [MaxLength(50)]
        public string? ReceiptNumber { get; set; }

        /// <summary>
        /// Sequential number for this register (for zero-gap numbering)
        /// </summary>
        public int? ReceiptSequenceNumber { get; set; }

        /// <summary>
        /// Cashier who processed the sale.
        /// </summary>
        [Required]
        public Guid CashierId { get; set; }

        [ForeignKey(nameof(CashierId))]
        public virtual User? Cashier { get; set; }

        /// <summary>
        /// Optional customer (can be null for walk-in sales).
        /// </summary>
        public Guid? CustomerId { get; set; }

        [ForeignKey(nameof(CustomerId))]
        public virtual Customer? Customer { get; set; }

        public DateTime SaleDate { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Status: "Completed", "Parked", "Voided", "Refunded"
        /// </summary>
        [Required]
        [MaxLength(20)]
        public string Status { get; set; } = "Completed";

        #endregion

        #region Amounts

        /// <summary>
        /// Sum of all item subtotals (before discount and tax).
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal Subtotal { get; set; }

        /// <summary>
        /// Discount amount applied.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal DiscountAmount { get; set; } = 0.00M;

        /// <summary>
        /// Discount percentage applied.
        /// </summary>
        [Column(TypeName = "decimal(5,2)")]
        public decimal DiscountPercentage { get; set; } = 0.00M;

        /// <summary>
        /// Tax amount (VAT, sales tax, etc.).
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal TaxAmount { get; set; }

        /// <summary>
        /// Final amount paid by customer.
        /// = Subtotal - DiscountAmount + TaxAmount
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// Loyalty points earned from this sale.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal LoyaltyPointsEarned { get; set; }

        /// <summary>
        /// Loyalty points redeemed in this sale.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal LoyaltyPointsRedeemed { get; set; }

        #endregion

        #region Payment

        /// <summary>
        /// Payment method: Cash, Card, MobileMoney, etc.
        /// For split payments this is set to PaymentMethod.Split; individual methods are on SalePayments.
        /// </summary>
        [Required]
        public BMS.Core.Enums.PaymentMethod PaymentMethod { get; set; } = BMS.Core.Enums.PaymentMethod.Cash;

        /// <summary>
        /// Materialized payment status derived from SalePayments.
        /// Always recomputed before persistence via CreditCalculationHelper.
        /// Indexed for efficient credit/debt queries.
        /// </summary>
        public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Paid;

        /// <summary>
        /// Legacy: Amount tendered by customer.
        /// New code should derive TotalPaid from SalePayments instead.
        /// Retained for backward compatibility with pre-credit-system data.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal AmountPaid { get; set; }

        public virtual ICollection<SalePayment> SalePayments { get; set; } = new List<SalePayment>();

        /// <summary>
        /// Change given back to customer.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal ChangeGiven { get; set; } = 0.00M;

        /// <summary>
        /// Payment reference (transaction ID for card/mobile money).
        /// </summary>
        [MaxLength(100)]
        public string? PaymentReference { get; set; }

        #endregion

        #region Computed Financial State (Not Mapped)

        /// <summary>
        /// Sum of all SalePayment amounts. Falls back to AmountPaid for legacy data without SalePayment records.
        /// </summary>
        [NotMapped]
        public decimal TotalPaid =>
            SalePayments != null && SalePayments.Count > 0
                ? SalePayments.Sum(p => p.Amount)
                : AmountPaid;

        /// <summary>
        /// Outstanding balance the customer still owes.
        /// </summary>
        [NotMapped]
        public decimal BalanceDue => Math.Max(0m, TotalAmount - TotalPaid);

        /// <summary>
        /// Amount paid beyond TotalAmount (rare — typically handled as change).
        /// </summary>
        [NotMapped]
        public decimal Overpayment => Math.Max(0m, TotalPaid - TotalAmount);

        #endregion

        #region Immutable Temporal Snapshots

        /// <summary>
        /// Customer name frozen at point-of-sale.
        /// Preserved even if the Customer record is later renamed, merged, or deleted.
        /// </summary>
        [MaxLength(200)]
        public string? CustomerNameSnapshot { get; set; }

        /// <summary>
        /// Branch address frozen at point-of-sale.
        /// Preserved even if the branch relocates and its address is updated in Brain.
        /// </summary>
        [MaxLength(500)]
        public string? BranchAddressSnapshot { get; set; }

        /// <summary>
        /// JSON array of tax component breakdowns frozen at point-of-sale.
        /// Format: [{Name, Abbreviation, Rate, Amount}]
        /// Preserved even if the tenant's TaxProfile is later modified.
        /// </summary>
        public string? TaxBreakdownJson { get; set; }

        #endregion

        #region Status

        /// <summary>
        /// If voided, who voided it?
        /// </summary>
        public Guid? VoidedByUserId { get; set; }

        public DateTime? VoidedDate { get; set; }

        [MaxLength(500)]
        public string? VoidReason { get; set; }

        #endregion

        #region Sync

        public bool IsSynced { get; set; } = false;
        public DateTime? SyncedDate { get; set; }

        /// <summary>
        /// Monotonic sequence number from the originating POS register.
        /// Used for audit gap detection — if sequence 47 and 49 exist but 48 is missing,
        /// that signals a lost transaction.
        /// </summary>
        public long LocalSequence { get; set; }

        #endregion

        #region Navigation Properties

        public virtual ICollection<SaleItem>? SaleItems { get; set; }
        public virtual ICollection<Refund>? Refunds { get; set; }

        #endregion

        public Sale()
        {
            SaleNumber = string.Empty;
            PaymentMethod = BMS.Core.Enums.PaymentMethod.Cash;

            SaleItems = new HashSet<SaleItem>();
            Refunds = new HashSet<Refund>();
        }
    }
}