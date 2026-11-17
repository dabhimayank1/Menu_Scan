using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MenuScan.Models.Entities
{
    public class MenuItem : ITenantEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid RestaurantId { get; set; }
        public Restaurant? Restaurant { get; set; }

        [Required]
        public Guid CategoryId { get; set; }
        public Category? Category { get; set; }

        [Required]
        [MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Description { get; set; }

        [Column(TypeName = "decimal(18, 2)")]
        public decimal Price { get; set; }

        [MaxLength(500)]
        public string? ImageUrl { get; set; }

        [NotMapped]
        public string? SafeImageUrl => MenuScan.Services.FileHelper.SafeImageUrl(ImageUrl);

        public bool IsVeg { get; set; } = true;
        public bool IsAvailable { get; set; } = true;
        public int DisplayOrder { get; set; } = 0;

        public ICollection<ItemCustomization> Customizations { get; set; } = new List<ItemCustomization>();
    }
}
