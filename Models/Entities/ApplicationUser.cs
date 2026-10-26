using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace MenuScan.Models.Entities
{
    public class ApplicationUser : IdentityUser
    {
        [Required]
        [MaxLength(100)]
        public string FullName { get; set; } = string.Empty;

        public Guid? RestaurantId { get; set; }
        public Restaurant? Restaurant { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
