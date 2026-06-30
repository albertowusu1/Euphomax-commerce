using BMS.Shared.DTOs.Sale;
using FluentValidation;

namespace BMS.Services.Validators
{
    public class SaleRequestValidator : AbstractValidator<SaleRequestDto>
    {
        public SaleRequestValidator()
        {
            RuleFor(x => x.BranchId)
                .NotEmpty().WithMessage("Branch ID is required");

            RuleFor(x => x.CashierId)
                .NotEmpty().WithMessage("Cashier ID is required");

            RuleFor(x => x.Items)
                .NotEmpty().WithMessage("Sale must have at least one item")
                .Must(items => items != null && items.Count > 0)
                .WithMessage("Sale must have at least one item");

            RuleForEach(x => x.Items).SetValidator(new SaleItemValidator());

            RuleFor(x => x.PaymentMethod)
                .NotEmpty().WithMessage("Payment method is required")
                .Must(pm => new[] { "Cash", "Card", "MobileMoney", "BankTransfer", "Credit" }.Contains(pm))
                .WithMessage("Invalid payment method");

            RuleFor(x => x.AmountPaid)
                .GreaterThan(0).WithMessage("Amount paid must be greater than 0");

            RuleFor(x => x.DiscountPercentage)
                .InclusiveBetween(0, 100).WithMessage("Discount percentage must be between 0 and 100");
        }
    }

    public class SaleItemValidator : AbstractValidator<SaleItemDto>
    {
        public SaleItemValidator()
        {
            RuleFor(x => x.ProductId)
                .NotEmpty().WithMessage("Product ID is required");

            RuleFor(x => x.Quantity)
                .GreaterThan(0).WithMessage("Quantity must be greater than 0");

            RuleFor(x => x.UnitPrice)
                .GreaterThan(0).WithMessage("Unit price must be greater than 0");

            RuleFor(x => x.DiscountAmount)
                .GreaterThanOrEqualTo(0).WithMessage("Discount cannot be negative");

            RuleFor(x => x.TaxAmount)
                .GreaterThanOrEqualTo(0).WithMessage("Tax cannot be negative");
        }
    }
}