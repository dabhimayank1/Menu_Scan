using System.ComponentModel.DataAnnotations;

namespace MenuScan.Models.Entities
{
    public class OrderStatusHistory
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid OrderId { get; set; }
        public Order? Order { get; set; }

        public OrderStatus OldStatus { get; set; }
        public OrderStatus NewStatus { get; set; }

        [MaxLength(100)]
        public string ChangedBy { get; set; } = "System";

        [MaxLength(250)]
        public string? Notes { get; set; }

        public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
