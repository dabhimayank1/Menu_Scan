using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MenuScan.Models.Entities
{
    public class Order : ITenantEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid RestaurantId { get; set; }
        public Restaurant? Restaurant { get; set; }

        [Required]
        public Guid TableId { get; set; }
        public DiningTable? Table { get; set; }

        [Required]
        [MaxLength(30)]
        public string OrderNumber { get; set; } = string.Empty;

        public OrderStatus Status { get; set; } = OrderStatus.New;

        [Column(TypeName = "decimal(18, 2)")]
        public decimal SubTotal { get; set; }

        [Column(TypeName = "decimal(5, 2)")]
        public decimal TaxRate { get; set; }

        [Column(TypeName = "decimal(18, 2)")]
        public decimal TaxAmount { get; set; }

        [Column(TypeName = "decimal(18, 2)")]
        public decimal TotalAmount { get; set; }

        [MaxLength(500)]
        public string? CustomerNotes { get; set; }

        [Required]
        [MaxLength(64)]
        public string OrderToken { get; set; } = Guid.NewGuid().ToString("N");

        [Required]
        [MaxLength(100)]
        public string IdempotencyKey { get; set; } = string.Empty;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
        public ICollection<OrderStatusHistory> StatusHistory { get; set; } = new List<OrderStatusHistory>();
        public Bill? Bill { get; set; }
    }
}
