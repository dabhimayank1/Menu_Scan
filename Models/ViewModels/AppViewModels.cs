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
        public List<Order> ActiveOrders { get; set; } = new();
    }

    public class OrderSubmissionDto
    {
        [Required]
        [MaxLength(120)]
        public string RestaurantSlug { get; set; } = string.Empty;

        [Required]
        [MaxLength(64)]
        public string TableToken { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? CustomerNotes { get; set; }

        [MaxLength(100)]
        public string? CustomerName { get; set; }

        [MaxLength(25)]
        public string? CustomerPhone { get; set; }

        [Required]
        [MaxLength(100)]
        public string IdempotencyKey { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public List<OrderItemRequestDto> Items { get; set; } = new();
    }

    public class OrderItemRequestDto
    {
        [Required]
        public Guid MenuItemId { get; set; }

        [Range(1, 100, ErrorMessage = "Quantity must be between 1 and 100.")]
        public int Quantity { get; set; }

        public List<Guid> CustomizationIds { get; set; } = new();

        [MaxLength(250)]
        public string? CustomizationSummary { get; set; }
    }

    public class OrderTrackingViewModel
    {
        public Order Order { get; set; } = default!;
        public Restaurant Restaurant { get; set; } = default!;
        public DiningTable Table { get; set; } = default!;
        public bool HasSubmittedFeedback { get; set; }
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

        [MaxLength(100)]
        public string? Reference { get; set; }
    }

    public class CancelOrderDto
    {
        [Required]
        public Guid OrderId { get; set; }

        [Required(ErrorMessage = "Cancellation reason is required.")]
        [MaxLength(500, ErrorMessage = "Cancellation reason cannot exceed 500 characters.")]
        public string Reason { get; set; } = string.Empty;
    }
}
