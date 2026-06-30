// ─────────────────────────────────────────────────────────────────────────────
//  PUBLIC SHOWCASE — CURATED EXCERPT (not the full production file)
//
//  A representative read path from the sales service, showing the platform's
//  server-side query discipline:
//
//    • Filtering, counting, ordering, and paging are ALL translated to SQL and
//      executed in the database — never by pulling rows into memory first. The
//      composable ApplySalesFilters / ApplyOrdering / ApplyPaging extension
//      methods build the IQueryable expression tree before a single row is read.
//    • AsNoTracking() because this is a read-only reporting projection.
//    • TagWith("Reporting") annotates the generated SQL so this query is
//      identifiable in PostgreSQL's pg_stat_statements / slow-query logs.
//    • The result is wrapped in the platform's uniform PagedResultDto<T>, and the
//      whole method in the uniform ApiResponseDto<T> envelope every endpoint returns.
//
//  The full SalesService is large (checkout, refunds, summaries, dashboards). Only
//  this one filtered-list method is reproduced here.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using BMS.Core.Interfaces;
using BMS.Data.Extensions;
using BMS.Services.Interfaces;
using BMS.Shared.DTOs.Common;
using BMS.Shared.DTOs.Sale;
using BMS.Shared.DTOs.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BMS.Services.Services
{
    public partial class SalesService : ISalesService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<SalesService> _logger;

        // NOTE: the real SalesService takes several more collaborators (mapper,
        // inventory, tenant settings, validator, notifications) and implements the
        // full sales surface. This excerpt keeps only what GetFilteredAsync needs.
        public SalesService(IUnitOfWork unitOfWork, ILogger<SalesService> logger)
        {
            _unitOfWork = unitOfWork;
            _logger = logger;
        }

        /// <summary>
        /// Returns a filtered, ordered, paged page of sales for the back-office
        /// reporting grid. Every clause below is server-translated: the database
        /// does the filtering, the COUNT, the ORDER BY, and the LIMIT/OFFSET.
        /// </summary>
        public async Task<ApiResponseDto<PagedResultDto<SalesListDto>>> GetFilteredAsync(
            SalesFilterParams filterParams, CancellationToken ct = default)
        {
            try
            {
                var query = _unitOfWork.Sales.GetQueryable()
                    .TagWith("Reporting") // identifiable in slow-query logs
                    .Include(s => s.Customer)
                    .Include(s => s.Branch)
                    .Include(s => s.Cashier)
                    .Include(s => s.SalePayments) // needed for the split-payment badge
                    .AsNoTracking()
                    .ApplySalesFilters(filterParams); // composable, SQL-translated WHERE

                // COUNT runs in the database against the same predicate — never count in memory.
                var totalCount = await query.CountAsync(ct);

                // ORDER BY + LIMIT/OFFSET are appended to the SAME query before materialization.
                query = query.ApplyOrdering(filterParams.SortBy ?? "SaleDate", filterParams.SortOrder ?? "desc");
                query = query.ApplyPaging(filterParams.PageNumber, filterParams.PageSize);

                // Only now is a single page of rows pulled into memory.
                var sales = await query.ToListAsync(ct);
                var dtos = sales.Select(MapToListDto).ToList();

                return ApiResponseDto<PagedResultDto<SalesListDto>>.SuccessResponse(
                    new PagedResultDto<SalesListDto>
                    {
                        Items = dtos,
                        TotalCount = totalCount,
                        PageNumber = filterParams.PageNumber,
                        PageSize = filterParams.PageSize
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving filtered sales");
                return ApiResponseDto<PagedResultDto<SalesListDto>>.ErrorResponse("Failed to retrieve sales");
            }
        }

        // MapToListDto(Sale) — a small entity → list-DTO projection — is defined in
        // the full service and omitted here for brevity.
        private static partial SalesListDto MapToListDto(BMS.Core.Entities.Sale sale);
    }
}
