// Curated excerpt — representative AutoMapper profile.
using AutoMapper;
using BMS.Core.Entities;
using BMS.Shared.DTOs.Sale;

namespace BMS.Services.Mappings
{
    public class SaleMappingProfile : Profile
    {
        public SaleMappingProfile()
        {
            // Entity → Response DTO
            CreateMap<Sale, SaleResponseDto>()
                .ForMember(dest => dest.BranchName,
                    opt => opt.MapFrom(src => src.Branch != null ? src.Branch.BranchName : ""))
            .ForMember(dest => dest.CashierName,
             opt => opt.MapFrom(src => src.Cashier != null ? src.Cashier.FullName : ""))
                .ForMember(dest => dest.CustomerName,
                    opt => opt.MapFrom(src => src.Customer != null ? src.Customer.FullName : null))
                .ForMember(dest => dest.Items,
                    opt => opt.MapFrom(src => src.SaleItems))
                .ForMember(dest => dest.Payments,
                    opt => opt.MapFrom(src => src.SalePayments))
                .ForMember(dest => dest.IsSplitPayment,
                    opt => opt.MapFrom(src => src.SalePayments != null && src.SalePayments.Count > 1))
                .ForMember(dest => dest.PaymentStatus,
                    opt => opt.MapFrom(src => src.PaymentStatus.ToString()))
                .ForMember(dest => dest.TotalPaid,
                    opt => opt.MapFrom(src => src.TotalPaid))
                .ForMember(dest => dest.BalanceDue,
                    opt => opt.MapFrom(src => src.BalanceDue));

            CreateMap<SalePayment, SalePaymentResponseDto>()
                .ForMember(dest => dest.PaymentSource,
                    opt => opt.MapFrom(src => src.PaymentSource.ToString()));

            CreateMap<SaleItem, SaleItemResponseDto>()
                .ForMember(dest => dest.ProductName,
                    opt => opt.MapFrom(src => src.ProductName));

            // Request DTO → Entity
            CreateMap<SaleRequestDto, Sale>()
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.TenantId, opt => opt.Ignore())
                .ForMember(dest => dest.SaleNumber, opt => opt.Ignore())
                .ForMember(dest => dest.SaleDate, opt => opt.MapFrom(src => DateTime.UtcNow))
                .ForMember(dest => dest.Status, opt => opt.MapFrom(src => "Completed"))
                .ForMember(dest => dest.Subtotal, opt => opt.Ignore()) // Calculated
                .ForMember(dest => dest.TaxAmount, opt => opt.Ignore()) // Calculated
                .ForMember(dest => dest.TotalAmount, opt => opt.Ignore()) // Calculated
                .ForMember(dest => dest.ChangeGiven, opt => opt.Ignore()) // Calculated
                .ForMember(dest => dest.Branch, opt => opt.Ignore())
                .ForMember(dest => dest.Cashier, opt => opt.Ignore())
                .ForMember(dest => dest.Customer, opt => opt.Ignore())
                .ForMember(dest => dest.SaleItems, opt => opt.Ignore()) // Mapped separately
                .ForMember(dest => dest.Refunds, opt => opt.Ignore())
                .ForMember(dest => dest.CreatedDate, opt => opt.Ignore())
                .ForMember(dest => dest.UpdatedDate, opt => opt.Ignore())
                .ForMember(dest => dest.IsActive, opt => opt.Ignore())
                .ForMember(dest => dest.IsSynced, opt => opt.Ignore())
                .ForMember(dest => dest.SyncedDate, opt => opt.Ignore())
                .ForMember(dest => dest.VoidedByUserId, opt => opt.Ignore())
                .ForMember(dest => dest.VoidedDate, opt => opt.Ignore())
                .ForMember(dest => dest.VoidReason, opt => opt.Ignore());

            CreateMap<SaleItemDto, SaleItem>()
                .ForMember(dest => dest.Id, opt => opt.Ignore())
                .ForMember(dest => dest.TenantId, opt => opt.Ignore())
                .ForMember(dest => dest.SaleId, opt => opt.Ignore())
                .ForMember(dest => dest.ProductName, opt => opt.Ignore()) // Loaded from Product
                .ForMember(dest => dest.LineTotal, opt => opt.Ignore()) // Calculated
                .ForMember(dest => dest.Product, opt => opt.Ignore())
                .ForMember(dest => dest.Sale, opt => opt.Ignore())
                .ForMember(dest => dest.BatchLot, opt => opt.Ignore()) // nav prop; BatchLotId maps by convention
                .ForMember(dest => dest.CreatedDate, opt => opt.Ignore())
                .ForMember(dest => dest.UpdatedDate, opt => opt.Ignore())
                .ForMember(dest => dest.IsActive, opt => opt.Ignore());
        }
    }
}