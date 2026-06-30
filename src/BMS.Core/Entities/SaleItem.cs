using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BMS.Core.Entities.Base;

namespace BMS.Core.Entities
{
    /// <summary>
    /// Individual line items within a sale transaction.
    /// Each product sold is one SaleItem.
    /// </summary>
    public class SaleItem : TenantEntity
    {
        [Required]
        public Guid SaleId { get; set; }

        [ForeignKey(nameof(SaleId))]
        public virtual Sale? Sale { get; set; }

        [Required]
        public Guid ProductId { get; set; }

        [ForeignKey(nameof(ProductId))]
        public virtual Product? Product { get; set; }

        /// <summary>
        /// Optional variant of the product sold (e.g., "Large", "Red").
        /// Null for non-variant products.
        /// </summary>
        public Guid? VariantId { get; set; }

        [ForeignKey(nameof(VariantId))]
        public virtual ProductVariant? Variant { get; set; }

        /// <summary>
        /// Product name at time of sale (snapshot - in case product is renamed later).
        /// </summary>
        [Required]
        [MaxLength(200)]
        public string ProductName { get; set; }

        /// <summary>
        /// Unit price at time of sale.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal UnitPrice { get; set; }

        /// <summary>
        /// Quantity sold. decimal(18,3) to support weighed / loose goods (e.g. 0.75 kg).
        /// </summary>
        [Column(TypeName = "decimal(18,3)")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// Discount applied to this line item.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal DiscountAmount { get; set; } = 0.00M;

        /// <summary>
        /// Tax applied to this line item.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal TaxAmount { get; set; }

        /// <summary>
        /// Line total = (UnitPrice * Quantity) - DiscountAmount + TaxAmount
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal LineTotal { get; set; }

        /// <summary>
        /// Quantity of this item that has been refunded. decimal(18,3) to allow partial
        /// returns of weighed / loose goods (e.g. return 0.25 kg of a 0.75 kg line).
        /// </summary>
        [Column(TypeName = "decimal(18,3)")]
        public decimal RefundedQuantity { get; set; } = 0m;

        /// <summary>
        /// Tax-authority category badge at time of sale (e.g., "A", "E", "Z").
        /// Snapshot from Product.TaxCategory — preserved for audit trail.
        /// </summary>
        [MaxLength(5)]
        public string? TaxBadge { get; set; }

        /// <summary>
        /// The batch/lot dispensed for this line item (batch/lot tracking).
        /// Null for products where Product.TrackBatch = false.
        /// Used for recall traceability: "which customers received Batch X?"
        /// </summary>
        public Guid? BatchLotId { get; set; }

        [ForeignKey(nameof(BatchLotId))]
        public virtual BatchLot? BatchLot { get; set; }

        public SaleItem()
        {
            ProductName = string.Empty;
        }
    }
}