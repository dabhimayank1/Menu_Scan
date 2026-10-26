using System.ComponentModel.DataAnnotations;

namespace MenuScan.Models.Entities
{
    public class Category : ITenantEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid RestaurantId { get; set; }
        public Restaurant? Restaurant { get; set; }

        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        public int DisplayOrder { get; set; } = 0;
        public bool IsActive { get; set; } = true;

        public ICollection<MenuItem> MenuItems { get; set; } = new List<MenuItem>();
    }
}
