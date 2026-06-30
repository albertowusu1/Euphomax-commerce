using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BMS.Core.Entities.Base
{
    /// <summary>
    /// Base entity for all tenant-scoped entities in the multi-tenant system.
    /// EVERY business entity (except Tenant itself) MUST inherit from this.
    /// This ensures tenant isolation at the entity level.
    /// </summary>
    public abstract class TenantEntity : AuditableEntity
    {
        /// <summary>
        /// The tenant (company) this entity belongs to.
        /// CRITICAL: This field enables multi-tenant data isolation.
        /// Global query filters will automatically filter by this field.
        /// </summary>
        [Required]
        public Guid TenantId { get; set; }

        /// <summary>
        /// Navigation property to the parent Tenant.
        /// Marked as virtual to support lazy loading if enabled.
        /// </summary>
        [ForeignKey(nameof(TenantId))]
        public virtual Tenant? Tenant { get; set; }
    }
}