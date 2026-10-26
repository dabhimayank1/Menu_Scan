using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MenuScan.Models.Entities
{
    public class OrderItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid OrderId { get; set; }
        public Order? Order { get; set; }

        public Guid? MenuItemId { get; set; }
        public MenuItem? MenuItem { get; set; }

        [Required]
        [MaxLength(120)]
        public string ItemNameSnapshot { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18, 2)")]
        public decimal UnitPriceSnapshot { get; set; }

        public int Quantity { get; set; } = 1;

        [Column(TypeName = "decimal(18, 2)")]
        public decimal TotalPrice { get; set; }

        [MaxLength(250)]
        public string? CustomizationSummary { get; set; }
    }
}
