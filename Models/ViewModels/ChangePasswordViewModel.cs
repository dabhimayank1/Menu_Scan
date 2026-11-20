using System.ComponentModel.DataAnnotations;

namespace MenuScan.Models.ViewModels
{
    public class ChangePasswordViewModel
    {
        [Display(Name = "Current Email")]
        public string? CurrentEmail { get; set; }

        [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
        [MaxLength(150, ErrorMessage = "Email cannot exceed 150 characters.")]
        [Display(Name = "New Email Address")]
        public string? NewEmail { get; set; }

        [Required(ErrorMessage = "Current password is required to verify your identity.")]
        [DataType(DataType.Password)]
        [Display(Name = "Current Password")]
        public string CurrentPassword { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Display(Name = "New Secure Password")]
        public string? NewPassword { get; set; }

        [DataType(DataType.Password)]
        [Compare("NewPassword", ErrorMessage = "New password and confirmation password do not match.")]
        [Display(Name = "Confirm New Password")]
        public string? ConfirmPassword { get; set; }
    }
}
