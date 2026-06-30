using System.Linq.Expressions;
using BMS.Core.Entities;
using BMS.Shared.DTOs.Customer;
using BMS.Shared.DTOs.Inventory;
using BMS.Shared.DTOs.Sales;

namespace BMS.Data.Extensions
{
    public static class QueryableExtensions
    {
        public static IQueryable<Sale> ApplySalesFilters(
            this IQueryable<Sale> query,
            SalesFilterParams filters)
        {
            if (filters.StartDate.HasValue)
            {
                // Ensure we get the start of the day in UTC if needed, or just compare
                // Assuming filters.StartDate is already in correct timezone or UTC
                var start = filters.StartDate.Value.Kind == DateTimeKind.Utc ? filters.StartDate.Value : DateTime.SpecifyKind(filters.StartDate.Value, DateTimeKind.Utc);
                query = query.Where(s => s.SaleDate >= start);
            }

            if (filters.EndDate.HasValue)
            {
                // End of the day
                var endVal = filters.EndDate.Value.Date.AddDays(1).AddTicks(-1);
                var end = endVal.Kind == DateTimeKind.Utc ? endVal : DateTime.SpecifyKind(endVal, DateTimeKind.Utc);
                query = query.Where(s => s.SaleDate <= end);
            }

            if (!string.IsNullOrWhiteSpace(filters.PaymentMethod) && Enum.TryParse<BMS.Core.Enums.PaymentMethod>(filters.PaymentMethod, true, out var pmEnum))
            {
                query = query.Where(s => s.PaymentMethod == pmEnum);
            }

            if (filters.MinTotalAmount.HasValue)
            {
                query = query.Where(s => s.TotalAmount >= filters.MinTotalAmount.Value);
            }

            if (filters.MaxTotalAmount.HasValue)
            {
                query = query.Where(s => s.TotalAmount <= filters.MaxTotalAmount.Value);
            }

            if (filters.LocationId.HasValue)
            {
                query = query.Where(s => s.BranchId == filters.LocationId.Value);
            }

            if (filters.CustomerId.HasValue)
            {
                query = query.Where(s => s.CustomerId == filters.CustomerId.Value);
            }

            if (!string.IsNullOrWhiteSpace(filters.Status))
            {
                var status = filters.Status.ToLower();
                query = query.Where(s => s.Status.ToLower() == status);
            }

            if (!string.IsNullOrWhiteSpace(filters.SearchTerm))
            {
                var term = filters.SearchTerm.ToLower();
                query = query.Where(s => 
                    s.SaleNumber.ToLower().Contains(term) || 
                    (s.Customer != null && s.Customer.FullName.ToLower().Contains(term)));
            }

            return query;
        }

        public static IQueryable<Inventory> ApplyInventoryFilters(
            this IQueryable<Inventory> query,
            InventoryFilterParams filters)
        {
            if (!string.IsNullOrWhiteSpace(filters.SearchTerm))
            {
                var term = filters.SearchTerm.ToLower();
                query = query.Where(i => 
                    i.Product!.ProductName.ToLower().Contains(term) || 
                    i.Product.ProductCode.ToLower().Contains(term));
            }

            if (filters.StartDate.HasValue)
            {
                var start = filters.StartDate.Value.Kind == DateTimeKind.Utc ? filters.StartDate.Value : DateTime.SpecifyKind(filters.StartDate.Value, DateTimeKind.Utc);
                query = query.Where(i => (i.UpdatedDate ?? i.CreatedDate) >= start);
            }

            if (filters.EndDate.HasValue)
            {
                var endVal = filters.EndDate.Value.Date.AddDays(1).AddTicks(-1);
                var end = endVal.Kind == DateTimeKind.Utc ? endVal : DateTime.SpecifyKind(endVal, DateTimeKind.Utc);
                query = query.Where(i => (i.UpdatedDate ?? i.CreatedDate) <= end);
            }

            if (filters.BranchId.HasValue)
            {
                query = query.Where(i => i.BranchId == filters.BranchId.Value);
            }

            if (filters.CategoryId.HasValue)
            {
                query = query.Where(i => i.Product!.CategoryId == filters.CategoryId.Value);
            }

            if (!string.IsNullOrWhiteSpace(filters.StockStatus))
            {
                if (filters.StockStatus == "LowStock")
                {
                    query = query.Where(i => i.QuantityInStock <= i.ReorderLevel && i.QuantityInStock > 0);
                }
                else if (filters.StockStatus == "OutOfStock")
                {
                    query = query.Where(i => i.QuantityInStock <= 0);
                }
            }

            if (filters.MinStock.HasValue)
            {
                query = query.Where(i => i.QuantityInStock >= filters.MinStock.Value);
            }

            if (filters.MaxStock.HasValue)
            {
                query = query.Where(i => i.QuantityInStock <= filters.MaxStock.Value);
            }

            if (filters.MinPrice.HasValue)
            {
                query = query.Where(i => i.UnitCost >= filters.MinPrice.Value);
            }

            if (filters.MaxPrice.HasValue)
            {
                query = query.Where(i => i.UnitCost <= filters.MaxPrice.Value);
            }

            return query;
        }

        public static IQueryable<T> ApplyPaging<T>(
            this IQueryable<T> query,
            int pageNumber,
            int pageSize)
        {
            return query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize);
        }

        public static IQueryable<T> ApplyOrdering<T>(
            this IQueryable<T> query,
            string? sortBy,
            string? sortOrder)
        {
            if (string.IsNullOrWhiteSpace(sortBy))
                return query;

            var parameter = Expression.Parameter(typeof(T), "x");
            var property = Expression.Property(parameter, sortBy);
            var lambda = Expression.Lambda(property, parameter);

            var methodName = sortOrder?.ToLower() == "desc"
                ? "OrderByDescending"
                : "OrderBy";

            var resultExpression = Expression.Call(
                typeof(Queryable),
                methodName,
                new[] { typeof(T), property.Type },
                query.Expression,
                Expression.Quote(lambda));

            return query.Provider.CreateQuery<T>(resultExpression);
        }

        public static IQueryable<Product> ApplyProductFilters(
            this IQueryable<Product> query,
            string? searchTerm = null,
            Guid? categoryId = null,
            Guid? supplierId = null,
            decimal? minPrice = null,
            decimal? maxPrice = null,
            bool? trackInventory = null,
            bool? isActive = null)
        {
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var lowerSearch = searchTerm.ToLower();
                query = query.Where(p =>
                    p.ProductName.ToLower().Contains(lowerSearch) ||
                    (p.ProductCode != null && p.ProductCode.ToLower().Contains(lowerSearch)) ||
                    (p.Barcode != null && p.Barcode.ToLower().Contains(lowerSearch)) ||
                    (p.Description != null && p.Description.ToLower().Contains(lowerSearch)));
            }

            if (categoryId.HasValue)
            {
                query = query.Where(p => p.CategoryId == categoryId.Value);
            }

            if (supplierId.HasValue)
            {
                query = query.Where(p => p.SupplierId == supplierId.Value);
            }

            if (minPrice.HasValue)
            {
                query = query.Where(p => p.SellingPrice >= minPrice.Value);
            }

            if (maxPrice.HasValue)
            {
                query = query.Where(p => p.SellingPrice <= maxPrice.Value);
            }

            if (trackInventory.HasValue)
            {
                query = query.Where(p => p.TrackInventory == trackInventory.Value);
            }

            if (isActive.HasValue)
            {
                query = query.Where(p => p.IsActive == isActive.Value);
            }

            return query;
        }

        public static IQueryable<Supplier> ApplySupplierFilters(
            this IQueryable<Supplier> query,
            string? searchTerm = null)
        {
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var lowerSearch = searchTerm.ToLower();
                query = query.Where(s =>
                    s.SupplierName.ToLower().Contains(lowerSearch) ||
                    (s.SupplierCode != null && s.SupplierCode.ToLower().Contains(lowerSearch)) ||
                    (s.ContactPerson != null && s.ContactPerson.ToLower().Contains(lowerSearch)) ||
                    (s.Email != null && s.Email.ToLower().Contains(lowerSearch)) ||
                    (s.PhoneNumber != null && s.PhoneNumber.ToLower().Contains(lowerSearch)));
            }

            return query;
        }

        public static IQueryable<Sale> ApplySaleFilters(
            this IQueryable<Sale> query,
            DateTime? startDate = null,
            DateTime? endDate = null,
            Guid? branchId = null,
            Guid? cashierId = null,
            Guid? customerId = null,
            string? paymentMethod = null,
            decimal? minAmount = null,
            decimal? maxAmount = null)
        {
            if (startDate.HasValue)
            {
                var start = startDate.Value.Kind == DateTimeKind.Utc ? startDate.Value : DateTime.SpecifyKind(startDate.Value, DateTimeKind.Utc);
                query = query.Where(s => s.SaleDate >= start);
            }

            if (endDate.HasValue)
            {
                var end = endDate.Value.Kind == DateTimeKind.Utc ? endDate.Value : DateTime.SpecifyKind(endDate.Value, DateTimeKind.Utc);
                query = query.Where(s => s.SaleDate <= end);
            }

            if (branchId.HasValue && branchId.Value != Guid.Empty)
            {
                query = query.Where(s => s.BranchId == branchId.Value);
            }

            if (cashierId.HasValue)
            {
                query = query.Where(s => s.CashierId == cashierId.Value);
            }

            if (customerId.HasValue && customerId.Value != Guid.Empty)
            {
                query = query.Where(s => s.CustomerId == customerId.Value);
            }

            if (!string.IsNullOrWhiteSpace(paymentMethod) && Enum.TryParse<BMS.Core.Enums.PaymentMethod>(paymentMethod, true, out var pmEnum))
            {
                query = query.Where(s => s.PaymentMethod == pmEnum);
            }

            if (minAmount.HasValue)
            {
                query = query.Where(s => s.TotalAmount >= minAmount.Value);
            }

            if (maxAmount.HasValue)
            {
                query = query.Where(s => s.TotalAmount <= maxAmount.Value);
            }

            return query;
        }

        public static IQueryable<Customer> ApplyCustomerFilters(
            this IQueryable<Customer> query,
            BMS.Shared.DTOs.Customer.CustomerFilterParams filters)
        {
            if (!string.IsNullOrWhiteSpace(filters.SearchTerm))
            {
                var term = filters.SearchTerm.ToLower();
                query = query.Where(c =>
                    c.FullName.ToLower().Contains(term) ||
                    (c.PhoneNumber != null && c.PhoneNumber.Contains(term)) ||
                    (c.Email != null && c.Email.ToLower().Contains(term)) ||
                    (c.TaxID != null && c.TaxID.ToLower().Contains(term)));
            }

            if (filters.MinLoyaltyPoints.HasValue)
            {
                query = query.Where(c => c.TotalLoyaltyPoints >= filters.MinLoyaltyPoints.Value);
            }

            if (filters.LoyaltyTierId.HasValue)
            {
                query = query.Where(c => c.LoyaltyTierId == filters.LoyaltyTierId.Value);
            }

            if (filters.IsActive.HasValue)
            {
                query = query.Where(c => c.IsActive == filters.IsActive.Value);
            }

            return query;
        }
    }
}