using System.Text.RegularExpressions;

namespace MenuScan.Services
{
    public static class SecurityRules
    {
        private static readonly Regex SlugDisallowedRegex = new(@"[^a-z0-9\-]", RegexOptions.Compiled);
        private static readonly Regex SlugMultiHyphenRegex = new(@"-{2,}", RegexOptions.Compiled);
        private static readonly Regex SlugFormatRegex = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

        private static readonly HashSet<string> ReservedSlugs = new(StringComparer.OrdinalIgnoreCase)
        {
            "admin", "api", "account", "superadmin", "billing", "kitchen", "track",
            "css", "js", "lib", "images", "img", "uploads", "home", "privacy", "error",
            "login", "logout", "register", "swagger", "hub", "orderhub", "customer",
            "reports", "pos", "floorplan", "settings", "tables", "menu", "category",
            "items", "ws", "health", "r", "t"
        };

        private static readonly HashSet<string> BlocklistedPasswords = new(StringComparer.OrdinalIgnoreCase)
        {
            "Admin@StrongPass2026!",
            "Staff@StrongPass2026!",
            "Secure2026",
            "Secure2026!",
            "Admin@123",
            "Staff@123",
            "123456",
            "password",
            "Password123!",
            "admin123",
            "Welcome@123",
            "P@ssword1",
            "Admin@2026",
            "Staff@2026",
            "Password@123"
        };

        public static IReadOnlyCollection<string> ReservedSlugList => ReservedSlugs;
        public static IReadOnlyCollection<string> BlocklistedPasswordList => BlocklistedPasswords;

        public static string Slugify(string? input, int maxLength = 60)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "shop";
            }

            var slug = input.Trim().ToLowerInvariant();
            slug = slug.Replace("&", "and");
            slug = slug.Replace(" ", "-").Replace("_", "-");
            slug = SlugDisallowedRegex.Replace(slug, "-");
            slug = SlugMultiHyphenRegex.Replace(slug, "-");
            slug = slug.Trim('-');

            if (slug.Length > maxLength)
            {
                slug = slug[..maxLength].TrimEnd('-');
            }

            return string.IsNullOrEmpty(slug) ? "shop" : slug;
        }

        public static bool IsValidSlug(string? slug, int minLength = 2, int maxLength = 60)
        {
            if (string.IsNullOrWhiteSpace(slug)) return false;
            if (slug.Length < minLength || slug.Length > maxLength) return false;
            return SlugFormatRegex.IsMatch(slug);
        }

        public static bool IsReservedSlug(string? slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return false;
            return ReservedSlugs.Contains(slug.Trim());
        }

        public static bool IsBlocklistedPassword(string? password)
        {
            if (string.IsNullOrWhiteSpace(password)) return false;
            return BlocklistedPasswords.Contains(password.Trim());
        }

        public static bool ValidatePassword(string? password, out string? errorMessage)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                errorMessage = "Password is required.";
                return false;
            }

            if (password.Length < 8)
            {
                errorMessage = "Password must be at least 8 characters long.";
                return false;
            }

            if (!password.Any(char.IsUpper))
            {
                errorMessage = "Password must contain at least one uppercase letter (A-Z).";
                return false;
            }

            if (!password.Any(char.IsLower))
            {
                errorMessage = "Password must contain at least one lowercase letter (a-z).";
                return false;
            }

            if (!password.Any(char.IsDigit))
            {
                errorMessage = "Password must contain at least one number (0-9).";
                return false;
            }

            if (!password.Any(ch => !char.IsLetterOrDigit(ch)))
            {
                errorMessage = "Password must contain at least one special character (!@#$%^&* etc.).";
                return false;
            }

            if (IsBlocklistedPassword(password))
            {
                errorMessage = "This password is too common or easily guessable. Please choose a stronger, unique password.";
                return false;
            }

            errorMessage = null;
            return true;
        }

        public static bool IsValidPhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return true; // Optional
            var digitsOnly = new string(phone.Where(char.IsDigit).ToArray());
            if (digitsOnly.Length < 7 || digitsOnly.Length > 15) return false;
            return Regex.IsMatch(phone.Trim(), @"^[\+]?[0-9\s\-\(\)]{7,20}$");
        }
    }
}
