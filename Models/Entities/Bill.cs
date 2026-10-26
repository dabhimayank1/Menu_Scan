using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MenuScan.Models.Entities
{
    public class Bill : ITenantEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid RestaurantId { get; set; }
        public Restaurant? Restaurant { get; set; }

        [Required]
        public Guid OrderId { get; set; }
        public Order? Order { get; set; }

        [Required]
        [MaxLength(30)]
        public string BillNumber { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18, 2)")]
        public decimal SubTotal { get; set; }

        [Column(TypeName = "decimal(5, 2)")]
        public decimal TaxRate { get; set; }

        [Column(TypeName = "decimal(18, 2)")]
        public decimal TaxAmount { get; set; }

        [Column(TypeName = "decimal(18, 2)")]
        public decimal GrandTotal { get; set; }

        public BillStatus Status { get; set; } = BillStatus.Unpaid;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? ClosedAtUtc { get; set; }

        public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    }
}
