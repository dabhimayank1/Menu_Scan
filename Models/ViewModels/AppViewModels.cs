using System.ComponentModel.DataAnnotations;
using MenuScan.Models.Entities;

namespace MenuScan.Models.ViewModels
{
    public class LoginViewModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; }
        public string? ReturnUrl { get; set; }
    }

    public class CustomerMenuViewModel
    {
        public Restaurant Restaurant { get; set; } = default!;
        public DiningTable Table { get; set; } = default!;
        public List<Category> Categories { get; set; } = new();
    }

    public class OrderSubmissionDto
    {
        [Required]
        public string RestaurantSlug { get; set; } = string.Empty;

        [Required]
        public string TableToken { get; set; } = string.Empty;

        public string? CustomerNotes { get; set; }

        [Required]
        public string IdempotencyKey { get; set; } = string.Empty;

        public List<OrderItemRequestDto> Items { get; set; } = new();
    }

    public class OrderItemRequestDto
    {
        public Guid MenuItemId { get; set; }
        public int Quantity { get; set; }
        public List<Guid> CustomizationIds { get; set; } = new();
        public string? CustomizationSummary { get; set; }
    }

    public class OrderTrackingViewModel
    {
        public Order Order { get; set; } = default!;
        public Restaurant Restaurant { get; set; } = default!;
        public DiningTable Table { get; set; } = default!;
    }

    public class TableQrViewModel
    {
        public DiningTable Table { get; set; } = default!;
        public string QrCodeDataUri { get; set; } = string.Empty;
        public string OrderUrl { get; set; } = string.Empty;
    }

    public class SettleBillDto
    {
        [Required]
        public Guid OrderId { get; set; }

        public PaymentMethod Method { get; set; } = PaymentMethod.Cash;

        public string? Reference { get; set; }
    }
}
