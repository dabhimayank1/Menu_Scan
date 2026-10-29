using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace MenuScan.Models.ViewModels
{
    public class ConfirmOnlinePaymentDto
    {
        [Required]
        public string OrderToken { get; set; } = string.Empty;

        public string PaymentReference { get; set; } = string.Empty;

        public string PaymentMethod { get; set; } = "UPI";
    }

    public class UpiPaymentDetailsDto
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string OrderNumber { get; set; } = string.Empty;
        public string TableNumber { get; set; } = string.Empty;
        public string RestaurantName { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = "8799020418";
        public decimal SubTotal { get; set; }
        public decimal TaxRate { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal GrandTotal { get; set; }
        public string UpiId { get; set; } = string.Empty;
        public string SelectedHandle { get; set; } = "upi";
        public string UpiUri { get; set; } = string.Empty;
        public string UpiQrDataUri { get; set; } = string.Empty;
        public bool IsAlreadyPaid { get; set; }
        public string? BillNumber { get; set; }
        public Dictionary<string, string> AppUris { get; set; } = new();
    }

    public class UpdateCustomerContactDto
    {
        [Required]
        public string OrderToken { get; set; } = string.Empty;
        public string? CustomerPhone { get; set; }
        public string? CustomerName { get; set; }
    }
}
