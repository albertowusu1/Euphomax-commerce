using System;

namespace BMS.Data.Services.Interfaces
{
    /// <summary>
    /// Service for managing the current tenant context throughout the application.
    /// Used by EF Core global query filters to automatically filter data by tenant.
    /// </summary>
    public interface ICurrentTenantService
    {
        /// <summary>
        /// Gets the current tenant ID.
        /// Returns null if no tenant is set (e.g., during system operations or vendor super-admin access).
        /// </summary>
        Guid? TenantId { get; }

        /// <summary>
        /// Sets the current tenant by ID.
        /// Called after user authentication to establish tenant context.
        /// </summary>
        /// <param name="tenantId">The tenant ID to set</param>
        void SetTenant(Guid tenantId);

        /// <summary>
        /// Sets the current tenant by license key.
        /// Used by WinForms offline POS during login.
        /// </summary>
        /// <param name="licenseKey">The tenant's license key</param>
        /// <returns>True if tenant was found and set, false otherwise</returns>
        bool SetTenantByLicenseKey(string licenseKey);

        /// <summary>
        /// Clears the current tenant context.
        /// Called during logout.
        /// </summary>
        void ClearTenant();

        /// <summary>
        /// Indicates if the current user is a vendor super-admin.
        /// Super-admins can bypass tenant filtering to see all data across tenants.
        /// </summary>
        bool IsSuperAdmin { get; set; }
    }
}