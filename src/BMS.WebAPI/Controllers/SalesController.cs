using BMS.Services.Interfaces;
using BMS.Services.Jobs;
using BMS.Shared.DTOs.Common;
using BMS.Shared.DTOs.Sale;
using BMS.Shared.DTOs.Sales;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BMS.WebAPI.Controllers
{
    /// <summary>
    /// API endpoints for managing sales transactions.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    [EnableRateLimiting("general")]
    public class SalesController : ControllerBase
    {
        private readonly ISalesService _salesService;
        private readonly ICacheService _cacheService;
        private readonly ILogger<SalesController> _logger;
        private readonly IBackgroundJobClient _backgroundJobClient;

        private const string SalesPrefix = "sales:";

        public SalesController(
            ISalesService salesService,
            ICacheService cacheService,
            ILogger<SalesController> logger,
            IBackgroundJobClient backgroundJobClient)
        {
            _salesService = salesService;
            _cacheService = cacheService;
            _logger = logger;
            _backgroundJobClient = backgroundJobClient;
        }



        /// <summary>
        /// Get all sales with pagination and filtering.
        /// </summary>
        /// <param name="filter">Filter parameters</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        [HttpGet]
        [ProducesResponseType(typeof(ApiResponseDto<PagedResultDto<SalesListDto>>), 200)]
        public async Task<IActionResult> GetAll(
            [FromQuery] SalesFilterParams filter,
            CancellationToken cancellationToken = default)
        {
            // Generate a cache key based on all filter parameters
            var cacheKey = $"{SalesPrefix}filter:{GetFilterHash(filter)}";

            try
            {
                var cached = await _cacheService.GetAsync<ApiResponseDto<PagedResultDto<SalesListDto>>>(cacheKey, cancellationToken);
                if (cached != null)
                {
                    _logger.LogInformation("Sales served from cache: {Key}", cacheKey);
                    return StatusCode(cached.StatusCode, cached);
                }
                _logger.LogInformation("Sales cache miss: {Key}", cacheKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sales cache get failed for key {Key}. Falling back to DB.", cacheKey);
            }

            var result = await _salesService.GetFilteredAsync(filter, cancellationToken);

            if (result.Success && result.Data != null)
            {
                try
                {
                    await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(5), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sales cache set failed for key {Key}.", cacheKey);
                }
            }

            return StatusCode(result.StatusCode, result);
        }

        private string GetFilterHash(SalesFilterParams filter)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"pg:{filter.PageNumber}:sz:{filter.PageSize}");
            if (filter.StartDate.HasValue) sb.Append($":start:{filter.StartDate.Value.Ticks}");
            if (filter.EndDate.HasValue) sb.Append($":end:{filter.EndDate.Value.Ticks}");
            if (filter.LocationId.HasValue) sb.Append($":br:{filter.LocationId}");
            if (!string.IsNullOrEmpty(filter.Status)) sb.Append($":st:{filter.Status}");
            if (!string.IsNullOrEmpty(filter.SearchTerm)) sb.Append($":q:{filter.SearchTerm}");
            if (!string.IsNullOrEmpty(filter.PaymentMethod)) sb.Append($":pm:{filter.PaymentMethod}");
            if (filter.MinTotalAmount.HasValue) sb.Append($":min:{filter.MinTotalAmount}");
            if (filter.MaxTotalAmount.HasValue) sb.Append($":max:{filter.MaxTotalAmount}");
            return CreateMD5(sb.ToString());
        }

        private string CreateMD5(string input)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var inputBytes = System.Text.Encoding.ASCII.GetBytes(input);
                var hashBytes = md5.ComputeHash(inputBytes);
                return Convert.ToHexString(hashBytes);
            }
        }

        /// <summary>
        /// Get sale by ID.
        /// </summary>
        /// <param name="id">Sale ID</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        [HttpGet("{id}")]
        [ProducesResponseType(typeof(ApiResponseDto<SaleResponseDto>), 200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> GetById(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"{SalesPrefix}{id}";

            try
            {
                var cached = await _cacheService.GetAsync<ApiResponseDto<SaleResponseDto>>(cacheKey, cancellationToken);
                if (cached != null)
                {
                    _logger.LogInformation("Sale {SaleId} served from cache", id);
                    return StatusCode(cached.StatusCode, cached);
                }
                _logger.LogInformation("Sale {SaleId} cache miss", id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sales cache get failed for {SaleId}. Falling back to DB.", id);
            }

            var result = await _salesService.GetByIdAsync(id, cancellationToken);

            if (result.Success && result.Data != null)
            {
                try
                {
                    await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(15), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sales cache set failed for {SaleId}.", id);
                }
            }

            return StatusCode(result.StatusCode, result);
        }

        /// <summary>
        /// Get sales for a specific branch.
        /// </summary>
        /// <param name="branchId">Branch ID</param>
        /// <param name="pageNumber">Page number</param>
        /// <param name="pageSize">Items per page</param>
        /// <param name="startDate">Optional start date filter</param>
        /// <param name="endDate">Optional end date filter</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        [HttpGet("branch/{branchId}")]
        [ProducesResponseType(typeof(ApiResponseDto<PagedResultDto<SaleResponseDto>>), 200)]
        public async Task<IActionResult> GetByBranch(
            Guid branchId,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"{SalesPrefix}branch:{branchId}:pg:{pageNumber}:sz:{pageSize}:from:{startDate?.ToString("O") ?? "null"}:to:{endDate?.ToString("O") ?? "null"}";

            try
            {
                var cached = await _cacheService.GetAsync<ApiResponseDto<PagedResultDto<SaleResponseDto>>>(cacheKey, cancellationToken);
                if (cached != null)
                {
                    _logger.LogInformation("Sales by branch served from cache: {Key}", cacheKey);
                    return StatusCode(cached.StatusCode, cached);
                }
                _logger.LogInformation("Sales by branch cache miss: {Key}", cacheKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sales cache get failed for key {Key}. Falling back to DB.", cacheKey);
            }

            var result = await _salesService.GetByBranchAsync(
                branchId,
                pageNumber,
                pageSize,
                startDate,
                endDate,
                cancellationToken);

            if (result.Success && result.Data != null)
            {
                try
                {
                    await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(15), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sales cache set failed for key {Key}.", cacheKey);
                }
            }

            return StatusCode(result.StatusCode, result);
        }

        /// <summary>
        /// Get total sales amount for a branch and date range.
        /// </summary>
        /// <param name="branchId">Branch ID</param>
        /// <param name="startDate">Start date</param>
        /// <param name="endDate">End date</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        [HttpGet("branch/{branchId}/total")]
        [ProducesResponseType(typeof(ApiResponseDto<decimal>), 200)]
        public async Task<IActionResult> GetTotalSales(
            Guid branchId,
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"{SalesPrefix}branch:{branchId}:total:from:{startDate.ToString("O")}:to:{endDate.ToString("O")}";

            try
            {
                var cached = await _cacheService.GetAsync<ApiResponseDto<decimal>>(cacheKey, cancellationToken);
                if (cached != null)
                {
                    _logger.LogInformation("Sales total served from cache: {Key}", cacheKey);
                    return StatusCode(cached.StatusCode, cached);
                }
                _logger.LogInformation("Sales total cache miss: {Key}", cacheKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sales cache get failed for key {Key}. Falling back to DB.", cacheKey);
            }

            var result = await _salesService.GetTotalSalesAsync(
                branchId,
                startDate,
                endDate,
                cancellationToken);

            if (result.Success)
            {
                try
                {
                    await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(15), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sales cache set failed for key {Key}.", cacheKey);
                }
            }

            return StatusCode(result.StatusCode, result);
        }

        /// <summary>
        /// Create a new sale transaction.
        /// </summary>
        /// <param name="request">Sale data including items</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        [HttpPost]
        [ProducesResponseType(typeof(ApiResponseDto<SaleResponseDto>), 201)]
        [ProducesResponseType(400)]
        public async Task<IActionResult> Create(
            [FromBody] SaleRequestDto request,
            CancellationToken cancellationToken = default)
        {
            var result = await _salesService.CreateAsync(request, cancellationToken);

            if (result.Success && result.Data != null)
            {
                try
                {
                    await _cacheService.RemoveByPrefixAsync(SalesPrefix, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sales cache invalidation failed after create.");
                }

                return CreatedAtAction(
                    nameof(GetById),
                    new { id = result.Data.Id },
                    result);
            }

            return StatusCode(result.StatusCode, result);
        }

        /// <summary>
        /// Get filtered and paginated sales with advanced search
        /// </summary>
        /// <param name="filterParams">Filtering and paging parameters for sales.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        [HttpGet("filter")]
        [ProducesResponseType(typeof(ApiResponseDto<PagedResultDto<SalesReceiptDto>>), 200)]
        public async Task<IActionResult> GetFilteredSales(
            [FromQuery] SalesFilterParams filterParams,
            CancellationToken cancellationToken = default)
        {
            // Convert dates to UTC
            if (filterParams.StartDate.HasValue)
            {
                filterParams.StartDate = filterParams.StartDate.Value.Kind == DateTimeKind.Utc
                    ? filterParams.StartDate.Value
                    : DateTime.SpecifyKind(filterParams.StartDate.Value, DateTimeKind.Utc);
            }

            if (filterParams.EndDate.HasValue)
            {
                filterParams.EndDate = filterParams.EndDate.Value.Kind == DateTimeKind.Utc
                    ? filterParams.EndDate.Value
                    : DateTime.SpecifyKind(filterParams.EndDate.Value, DateTimeKind.Utc);
            }

            // Use query string to build a stable cache key for arbitrary filters
            var cacheKey = $"{SalesPrefix}filter:{Request.QueryString.Value?.ToLower() ?? string.Empty}";

            try
            {
                var cached = await _cacheService.GetAsync<ApiResponseDto<PagedResultDto<SalesListDto>>>(cacheKey, cancellationToken);
                if (cached != null)
                {
                    _logger.LogInformation("Filtered sales served from cache: {Key}", cacheKey);
                    return StatusCode(cached.StatusCode, cached);
                }
                _logger.LogInformation("Filtered sales cache miss: {Key}", cacheKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sales cache get failed for key {Key}. Falling back to DB.", cacheKey);
            }

            var result = await _salesService.GetFilteredAsync(filterParams, cancellationToken);

            if (result.Success && result.Data != null)
            {
                try
                {
                    await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(15), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sales cache set failed for key {Key}.", cacheKey);
                }
            }

            return StatusCode(result.StatusCode, result);
        }

        [HttpGet("summary")]
        [ProducesResponseType(typeof(ApiResponseDto<SalesSummaryDto>), 200)]
        public async Task<IActionResult> GetSummary(
            [FromQuery] DateTime? startDate,
            [FromQuery] DateTime? endDate,
            [FromQuery] Guid? locationId,
            CancellationToken ct = default)
        {
            var cacheKey = $"sales:summary:{startDate}:{endDate}:{locationId}";
            
            var cached = await _cacheService.GetAsync<ApiResponseDto<SalesSummaryDto>>(cacheKey, ct);
            if (cached != null) return StatusCode(cached.StatusCode, cached);

            var result = await _salesService.GetSummaryAsync(startDate, endDate, locationId, ct);
            
            if (result.Success)
                await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(10), ct);

            return StatusCode(result.StatusCode, result);
        }

        [HttpGet("{id}/receipt")]
        [ProducesResponseType(typeof(ApiResponseDto<SalesReceiptDto>), 200)]
        public async Task<IActionResult> GetReceipt(Guid id, CancellationToken ct = default)
        {
            var result = await _salesService.GetReceiptAsync(id, ct);
            return StatusCode(result.StatusCode, result);
        }

        [HttpGet("top-products")]
        [ProducesResponseType(typeof(ApiResponseDto<List<TopProductDto>>), 200)]
        public async Task<IActionResult> GetTopProducts(
            [FromQuery] DateTime? startDate,
            [FromQuery] DateTime? endDate,
            [FromQuery] Guid? locationId,
            [FromQuery] int top = 5,
            CancellationToken ct = default)
        {
            var cacheKey = $"sales:top-products:{startDate}:{endDate}:{locationId}:{top}";
            
            var cached = await _cacheService.GetAsync<ApiResponseDto<List<TopProductDto>>>(cacheKey, ct);
            if (cached != null) return StatusCode(cached.StatusCode, cached);

            var result = await _salesService.GetTopProductsAsync(startDate, endDate, locationId, top, ct);
            
            if (result.Success)
                await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(15), ct);

            return StatusCode(result.StatusCode, result);
        }

        [HttpGet("payment-breakdown")]
        [ProducesResponseType(typeof(ApiResponseDto<Dictionary<string, decimal>>), 200)]
        public async Task<IActionResult> GetPaymentBreakdown(
            [FromQuery] DateTime? startDate,
            [FromQuery] DateTime? endDate,
            [FromQuery] Guid? locationId,
            CancellationToken ct = default)
        {
            var cacheKey = $"sales:payment-breakdown:{startDate}:{endDate}:{locationId}";
            
            var cached = await _cacheService.GetAsync<ApiResponseDto<Dictionary<string, decimal>>>(cacheKey, ct);
            if (cached != null) return StatusCode(cached.StatusCode, cached);

            var result = await _salesService.GetPaymentBreakdownAsync(startDate, endDate, locationId, ct);
            
            if (result.Success)
                await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(15), ct);

            return StatusCode(result.StatusCode, result);
        }

        [HttpPost("{id}/void")]
        [ProducesResponseType(typeof(ApiResponseDto<bool>), 200)]
        public async Task<IActionResult> VoidSale(Guid id, [FromBody] VoidSaleRequestDto request, CancellationToken ct = default)
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userId, out var uid)) return Unauthorized();

            var result = await _salesService.VoidSaleAsync(id, request.Reason, uid, ct);
            
            if (result.Success)
                await _cacheService.RemoveByPrefixAsync(SalesPrefix, ct);

            return StatusCode(result.StatusCode, result);
        }

        /// <summary>
        /// Triggers a background job to export sales to Excel.
        /// </summary>
        [HttpPost("export")]
        [ProducesResponseType(typeof(ApiResponseDto<string>), 202)]
        public IActionResult ExportSales([FromBody] SalesFilterParams filter)
        {
            var userEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "unknown";
            var tenantIdClaim = User.FindFirst("TenantId")?.Value;
            
            if (!Guid.TryParse(tenantIdClaim, out var tenantId))
            {
                return BadRequest(ApiResponseDto<string>.ErrorResponse("Tenant ID not found in claims", 400));
            }

            _backgroundJobClient.Enqueue<ISalesExportJob>(job => job.GenerateSalesExportAsync(filter, userEmail, tenantId));

            return Accepted(ApiResponseDto<string>.SuccessResponse("Export started. You will be notified when ready.", 202));
        }

        [HttpPost("{id}/refund")]
        [ProducesResponseType(typeof(ApiResponseDto<bool>), 200)]
        public async Task<IActionResult> RefundSale(Guid id, [FromBody] RefundSaleRequestDto request, CancellationToken ct = default)
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userId, out var uid)) return Unauthorized();

            var result = await _salesService.RefundSaleAsync(id, request, uid, ct);
            
            if (result.Success)
                await _cacheService.RemoveByPrefixAsync(SalesPrefix, ct);

            return StatusCode(result.StatusCode, result);
        }
    }
}