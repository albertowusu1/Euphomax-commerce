using System;
using System.ComponentModel.DataAnnotations;

namespace BMS.Core.Entities.Base
{
    /// <summary>
    /// Base entity class providing common properties for all entities.
    /// All entities in the system inherit from this or its derived classes.
    /// </summary>
    public abstract class BaseEntity
    {
        /// <summary>
        /// Unique identifier for the entity.
        /// Uses Guid for better distribution and offline-first support.
        /// </summary>
        [Key]
        public Guid Id { get; set; }

        /// <summary>
        /// Indicates whether the entity is active in the system.
        /// Use this for soft deletes instead of actually removing records.
        /// Default is true (active).
        /// </summary>
        public bool IsActive { get; set; } = true;

        protected BaseEntity()
        {
            // Initialize ID in constructor so entities have IDs immediately upon creation
            // This is crucial for offline-first scenarios where you need IDs before syncing
            Id = Guid.NewGuid();
        }
    }
}