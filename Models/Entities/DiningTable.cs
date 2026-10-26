using System.ComponentModel.DataAnnotations;

namespace MenuScan.Models.Entities
{
    public class DiningTable : ITenantEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid RestaurantId { get; set; }
        public Restaurant? Restaurant { get; set; }

        [Required]
        [MaxLength(20)]
        public string TableNumber { get; set; } = string.Empty;

        [Required]
        [MaxLength(64)]
        public string Token { get; set; } = Guid.NewGuid().ToString("N")[..12];

        public int Capacity { get; set; } = 4;
        public bool IsActive { get; set; } = true;

        public ICollection<Order> Orders { get; set; } = new List<Order>();
    }
}
