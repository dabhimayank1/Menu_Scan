using System.ComponentModel.DataAnnotations;

namespace MenuScan.Models.Entities
{
    public class CustomerFeedback : ITenantEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid RestaurantId { get; set; }
        public Restaurant? Restaurant { get; set; }

        [Required]
        public Guid OrderId { get; set; }
        public Order? Order { get; set; }

        public Guid? TableId { get; set; }
        public DiningTable? Table { get; set; }

        [Range(1, 5)]
        public int OverallRating { get; set; } = 5;

        [Range(1, 5)]
        public int FoodRating { get; set; } = 5;

        [Range(1, 5)]
        public int ServiceRating { get; set; } = 5;

        [MaxLength(100)]
        public string? CustomerName { get; set; }

        [MaxLength(300)]
        public string? Tags { get; set; }

        [MaxLength(1000)]
        public string? Comment { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
