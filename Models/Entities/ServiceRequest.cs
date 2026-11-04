using System.ComponentModel.DataAnnotations;

namespace MenuScan.Models.Entities
{
    public class ServiceRequest : ITenantEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid RestaurantId { get; set; }
        public Restaurant? Restaurant { get; set; }

        public Guid TableId { get; set; }
        public DiningTable? Table { get; set; }

        public Guid? OrderId { get; set; }
        public Order? Order { get; set; }

        [Required, MaxLength(50)]
        public string RequestType { get; set; } = "Assistance"; // Assistance, Water, Cutlery, Bill, Custom

        [MaxLength(200)]
        public string? Note { get; set; }

        public bool IsHandled { get; set; } = false;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime? HandledAtUtc { get; set; }

        [MaxLength(100)]
        public string? HandledBy { get; set; }
    }
}
