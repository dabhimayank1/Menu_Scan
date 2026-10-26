using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MenuScan.Models.Entities
{
    public class ItemCustomization
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid MenuItemId { get; set; }
        public MenuItem? MenuItem { get; set; }

        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18, 2)")]
        public decimal Price { get; set; } = 0.0m;

        public bool IsAvailable { get; set; } = true;
    }
}
