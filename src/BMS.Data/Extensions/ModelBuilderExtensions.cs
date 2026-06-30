using BMS.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace BMS.Data.Extensions
{
    /// <summary>
    /// Extension methods for ModelBuilder to configure performance-critical database indexes.
    /// These indexes are designed to optimize the most common query patterns in the BMS system.
    /// </summary>
    public static class ModelBuilderExtensions
    {
        /// <summary>
        /// Configures all performance indexes for frequently queried entities.
        /// Call this method after all entity configurations in OnModelCreating.
        /// </summary>
        public static void ConfigurePerformanceIndexes(this ModelBuilder modelBuilder)
        {
            // ===== PRODUCTS INDEXES =====
            // Products are the most frequently queried entity in POS operations
            
            // Fast lookup by product code (used in manual entry)
            modelBuilder.Entity<Product>()
                .HasIndex(p => p.ProductCode)
                .HasDatabaseName("IX_Products_ProductCode");
            
            // Fast lookup by barcode (used in scanning)
            modelBuilder.Entity<Product>()
                .HasIndex(p => p.Barcode)
                .HasDatabaseName("IX_Products_Barcode");
            
            // Category filtering within tenant
            modelBuilder.Entity<Product>()
                .HasIndex(p => new { p.TenantId, p.CategoryId })
                .HasDatabaseName("IX_Products_TenantId_CategoryId");
            
            // Active products listing (most common filter)
            modelBuilder.Entity<Product>()
                .HasIndex(p => new { p.TenantId, p.IsActive })
                .HasDatabaseName("IX_Products_TenantId_IsActive");
            
            // Supplier-based product queries
            modelBuilder.Entity<Product>()
                .HasIndex(p => new { p.TenantId, p.SupplierId })
                .HasDatabaseName("IX_Products_TenantId_SupplierId");
            
            // Price range queries for reporting
            modelBuilder.Entity<Product>()
                .HasIndex(p => new { p.TenantId, p.SellingPrice })
                .HasDatabaseName("IX_Products_TenantId_SellingPrice");

            // ===== SALES INDEXES =====
            // Critical for reporting and analytics
            
            // Date-based reporting (most common report filter)
            modelBuilder.Entity<Sale>()
                .HasIndex(s => s.SaleDate)
                .HasDatabaseName("IX_Sales_SaleDate");
            
            // Branch performance reports
            modelBuilder.Entity<Sale>()
                .HasIndex(s => new { s.TenantId, s.BranchId, s.SaleDate })
                .HasDatabaseName("IX_Sales_TenantId_BranchId_SaleDate");
            
            // Customer purchase history
            modelBuilder.Entity<Sale>()
                .HasIndex(s => new { s.TenantId, s.CustomerId })
                .HasDatabaseName("IX_Sales_TenantId_CustomerId");
            
            // Cashier performance tracking
            modelBuilder.Entity<Sale>()
                .HasIndex(s => new { s.TenantId, s.CashierId, s.SaleDate })
                .HasDatabaseName("IX_Sales_TenantId_CashierId_SaleDate");
            
            // Payment method reporting
            modelBuilder.Entity<Sale>()
                .HasIndex(s => new { s.TenantId, s.PaymentMethod, s.SaleDate })
                .HasDatabaseName("IX_Sales_TenantId_PaymentMethod_SaleDate");
            
            // Status filtering (completed, voided, etc.)
            modelBuilder.Entity<Sale>()
                .HasIndex(s => new { s.TenantId, s.Status, s.SaleDate })
                .HasDatabaseName("IX_Sales_TenantId_Status_SaleDate");

            // ===== SALE ITEMS INDEXES =====
            // For order details and product sales analysis
            
            // Sale line items lookup
            modelBuilder.Entity<SaleItem>()
                .HasIndex(si => new { si.SaleId, si.ProductId })
                .HasDatabaseName("IX_SaleItems_SaleId_ProductId");
            
            // Product sales analysis
            modelBuilder.Entity<SaleItem>()
                .HasIndex(si => new { si.TenantId, si.ProductId })
                .HasDatabaseName("IX_SaleItems_TenantId_ProductId");

            // ===== INVENTORY INDEXES =====
            // Critical for real-time stock checks during POS operations
            
            // Primary inventory lookup (already unique in configuration, but explicit here).
            // ProductVariantId is part of the unique key so a variant product can hold one
            // stock row per variant per branch; base/non-variant rows
            // carry ProductVariantId = NULL.
            modelBuilder.Entity<Inventory>()
                .HasIndex(i => new { i.TenantId, i.BranchId, i.ProductId, i.ProductVariantId })
                .IsUnique()
                .HasDatabaseName("IX_Inventory_TenantId_BranchId_ProductId_VariantId");
            
            // Low stock alerts
            modelBuilder.Entity<Inventory>()
                .HasIndex(i => new { i.ProductId, i.QuantityInStock })
                .HasDatabaseName("IX_Inventory_ProductId_Quantity");
            
            // Branch stock reports
            modelBuilder.Entity<Inventory>()
                .HasIndex(i => new { i.TenantId, i.BranchId, i.QuantityInStock })
                .HasDatabaseName("IX_Inventory_TenantId_BranchId_Quantity");
            
            // Expiry date tracking
            modelBuilder.Entity<Inventory>()
                .HasIndex(i => new { i.TenantId, i.ExpiryDate })
                .HasDatabaseName("IX_Inventory_TenantId_ExpiryDate")
                .HasFilter("\"ExpiryDate\" IS NOT NULL");

            // ===== INVENTORY TRANSACTIONS INDEXES =====
            // For audit trails and stock movement history
            
            // Transaction history by inventory
            modelBuilder.Entity<InventoryTransaction>()
                .HasIndex(it => new { it.InventoryId, it.TransactionDate })
                .HasDatabaseName("IX_InventoryTransactions_InventoryId_Date");
            
            // Transaction type filtering
            modelBuilder.Entity<InventoryTransaction>()
                .HasIndex(it => new { it.TenantId, it.TransactionType, it.TransactionDate })
                .HasDatabaseName("IX_InventoryTransactions_TenantId_Type_Date");

            // ===== CUSTOMER INDEXES =====
            // For fast customer lookups during POS operations
            
            // Phone number lookup (common customer identification)
            modelBuilder.Entity<Customer>()
                .HasIndex(c => c.PhoneNumber)
                .HasDatabaseName("IX_Customers_PhoneNumber");
            
            // Email lookup
            modelBuilder.Entity<Customer>()
                .HasIndex(c => c.Email)
                .HasDatabaseName("IX_Customers_Email");
            
            // Active customers listing
            modelBuilder.Entity<Customer>()
                .HasIndex(c => new { c.TenantId, c.IsActive })
                .HasDatabaseName("IX_Customers_TenantId_IsActive");
            
            // Loyalty points queries
            modelBuilder.Entity<Customer>()
                .HasIndex(c => new { c.TenantId, c.TotalLoyaltyPoints })
                .HasDatabaseName("IX_Customers_TenantId_TotalLoyaltyPoints");

            // ===== USER INDEXES =====
            // For authentication and authorization
            
            // Email-based login (unique constraint already exists)
            modelBuilder.Entity<User>()
                .HasIndex(u => u.Email)
                .HasDatabaseName("IX_Users_Email");
            
            // Branch user listing
            modelBuilder.Entity<User>()
                .HasIndex(u => new { u.TenantId, u.BranchId })
                .HasDatabaseName("IX_Users_TenantId_BranchId");
            
            // Role-based queries
            modelBuilder.Entity<User>()
                .HasIndex(u => new { u.TenantId, u.Role, u.IsActive })
                .HasDatabaseName("IX_Users_TenantId_Role_IsActive");

            // ===== BRANCH INDEXES =====
            
            // Active branches listing
            modelBuilder.Entity<Branch>()
                .HasIndex(b => new { b.TenantId, b.IsActive })
                .HasDatabaseName("IX_Branches_TenantId_IsActive");

            // ===== CATEGORY INDEXES =====
            
            // Hierarchical category queries
            modelBuilder.Entity<Category>()
                .HasIndex(c => new { c.TenantId, c.ParentCategoryId })
                .HasDatabaseName("IX_Categories_TenantId_ParentCategory");
            
            // Active categories
            modelBuilder.Entity<Category>()
                .HasIndex(c => new { c.TenantId, c.IsActive })
                .HasDatabaseName("IX_Categories_TenantId_IsActive");

            // ===== SUPPLIER INDEXES =====
            
            // Active suppliers listing
            modelBuilder.Entity<Supplier>()
                .HasIndex(s => new { s.TenantId, s.IsActive })
                .HasDatabaseName("IX_Suppliers_TenantId_IsActive");

            // ===== EXPENSE INDEXES =====
            
            // Date-based expense reports
            modelBuilder.Entity<Expense>()
                .HasIndex(e => new { e.TenantId, e.ExpenseDate })
                .HasDatabaseName("IX_Expenses_TenantId_ExpenseDate");
            
            // Branch expense tracking
            modelBuilder.Entity<Expense>()
                .HasIndex(e => new { e.TenantId, e.BranchId, e.ExpenseDate })
                .HasDatabaseName("IX_Expenses_TenantId_BranchId_Date");
            
            // Approval workflow
            modelBuilder.Entity<Expense>()
                .HasIndex(e => new { e.TenantId, e.ApprovalStatus })
                .HasDatabaseName("IX_Expenses_TenantId_ApprovalStatus");

            // ===== AUDIT LOG INDEXES =====
            
            // Audit trail queries by user
            modelBuilder.Entity<AuditLog>()
                .HasIndex(al => new { al.TenantId, al.UserId, al.Timestamp })
                .HasDatabaseName("IX_AuditLogs_TenantId_UserId_Timestamp");
            
            // Entity audit history
            modelBuilder.Entity<AuditLog>()
                .HasIndex(al => new { al.EntityType, al.EntityId, al.Timestamp })
                .HasDatabaseName("IX_AuditLogs_EntityType_EntityId_Timestamp");
            
            // Action-based filtering
            modelBuilder.Entity<AuditLog>()
                .HasIndex(al => new { al.TenantId, al.Action, al.Timestamp })
                .HasDatabaseName("IX_AuditLogs_TenantId_Action_Timestamp");

            // ===== REFUND INDEXES =====
            
            // Refund history
            modelBuilder.Entity<Refund>()
                .HasIndex(r => new { r.TenantId, r.RefundDate })
                .HasDatabaseName("IX_Refunds_TenantId_RefundDate");
            
            // Branch refund tracking
            modelBuilder.Entity<Refund>()
                .HasIndex(r => new { r.TenantId, r.BranchId, r.RefundDate })
                .HasDatabaseName("IX_Refunds_TenantId_BranchId_Date");

            // ===== SUSPENDED SALES INDEXES =====
            
            // Pending suspended sales
            modelBuilder.Entity<SuspendedSale>()
                .HasIndex(ss => new { ss.TenantId, ss.Status, ss.SuspendedDate })
                .HasDatabaseName("IX_SuspendedSales_TenantId_Status_Date");
        }
    }
}