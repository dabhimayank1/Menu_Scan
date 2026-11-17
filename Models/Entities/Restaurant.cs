using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MenuScan.Models.Entities
{
    public class Restaurant
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        [MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [MaxLength(120)]
        public string Slug { get; set; } = string.Empty;

        [MaxLength(250)]
        public string? Address { get; set; }

        [MaxLength(30)]
        public string? Phone { get; set; }

        [MaxLength(100)]
        public string? UpiId { get; set; }

        [MaxLength(150)]
        public string? PayeeName { get; set; }

        [MaxLength(500)]
        public string? PaymentQrImageUrl { get; set; }

        [NotMapped]
        public string? SafePaymentQrImageUrl => MenuScan.Services.FileHelper.SafeImageUrl(PaymentQrImageUrl);

        [MaxLength(50)]
        public string? TaxNumber { get; set; }

        [Column(TypeName = "decimal(5, 2)")]
        public decimal DefaultTaxRate { get; set; } = 5.0m; // Default 5% GST

        [MaxLength(500)]
        public string? LogoUrl { get; set; }

        [NotMapped]
        public string? SafeLogoUrl => MenuScan.Services.FileHelper.SafeImageUrl(LogoUrl);

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        // Navigation properties
        public ICollection<ApplicationUser> Users { get; set; } = new List<ApplicationUser>();
        public ICollection<DiningTable> Tables { get; set; } = new List<DiningTable>();
        public ICollection<Category> Categories { get; set; } = new List<Category>();
        public ICollection<MenuItem> MenuItems { get; set; } = new List<MenuItem>();
        public ICollection<Order> Orders { get; set; } = new List<Order>();
    }
}
