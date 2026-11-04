using System.Security.Claims;
using MenuScan.Data;
using MenuScan.Models.Entities;
using MenuScan.Models.ViewModels;
using MenuScan.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MenuScan.Controllers
{
    [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin},{AppRoles.Cashier}")]
    public class AdminController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ITenantService _tenantService;
        private readonly IQRService _qrService;
        private readonly ILogger<AdminController> _logger;
        private readonly IWebHostEnvironment _env;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IConfiguration _configuration;

        public AdminController(
            ApplicationDbContext context,
            ITenantService tenantService,
            IQRService qrService,
            ILogger<AdminController> logger,
            IWebHostEnvironment env,
            UserManager<ApplicationUser> userManager,
            IConfiguration configuration)
        {
            _context = context;
            _tenantService = tenantService;
            _qrService = qrService;
            _logger = logger;
            _env = env;
            _userManager = userManager;
            _configuration = configuration;
        }

        private async Task<(Restaurant? Restaurant, IActionResult? Result)> GetCurrentRestaurantAsync()
        {
            var restaurantId = _tenantService.CurrentRestaurantId;
            if (!restaurantId.HasValue)
            {
                if (User.IsInRole(AppRoles.SuperAdmin))
                {
                    return (null, RedirectToAction("Index", "SuperAdmin"));
                }
                return (null, Forbid());
            }

            var r = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == restaurantId.Value);
            if (r == null) return (null, NotFound("Restaurant not configured."));
            return (r, null);
        }

        [HttpGet]
        [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> Index()
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var todayUtc = DateTime.UtcNow.Date;
            var todayBills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant!.Id && b.Status == BillStatus.Paid && b.CreatedAtUtc >= todayUtc)
                .ToListAsync();

            var todayOrdersCount = await _context.Orders
                .IgnoreQueryFilters()
                .CountAsync(o => o.RestaurantId == restaurant!.Id && o.CreatedAtUtc >= todayUtc);

            var tablesCount = await _context.DiningTables
                .IgnoreQueryFilters()
                .CountAsync(t => t.RestaurantId == restaurant!.Id);

            var activeItemsCount = await _context.MenuItems
                .IgnoreQueryFilters()
                .CountAsync(m => m.RestaurantId == restaurant!.Id && m.IsAvailable);

            ViewBag.Restaurant = restaurant;
            ViewBag.TodayRevenue = todayBills.Sum(b => b.GrandTotal);
            ViewBag.TodayOrders = todayOrdersCount;
            ViewBag.TablesCount = tablesCount;
            ViewBag.ActiveItemsCount = activeItemsCount;

            return View();
        }

        [HttpGet]
        public async Task<IActionResult> Tables()
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var tables = await _context.DiningTables
                .IgnoreQueryFilters()
                .Where(t => t.RestaurantId == restaurant!.Id)
                .OrderBy(t => t.TableNumber)
                .ToListAsync();

            ViewBag.Restaurant = restaurant;
            return View(tables);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddTable(string tableNumber, int capacity)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            if (string.IsNullOrWhiteSpace(tableNumber))
            {
                TempData["Error"] = "Table number cannot be empty.";
                return RedirectToAction(nameof(Tables));
            }

            var table = new DiningTable
            {
                RestaurantId = restaurant!.Id,
                TableNumber = tableNumber.Trim(),
                Capacity = capacity > 0 ? capacity : 4,
                Token = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
                IsActive = true
            };

            _context.DiningTables.Add(table);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Table {table.TableNumber} added successfully.";

            return RedirectToAction(nameof(Tables));
        }

        [HttpPost]
        [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> ToggleTable(Guid id)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var table = await _context.DiningTables
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == id && t.RestaurantId == restaurant!.Id);

            if (table == null) return NotFound();

            table.IsActive = !table.IsActive;
            await _context.SaveChangesAsync();

            return Ok(new { success = true, isActive = table.IsActive });
        }

        [HttpGet]
        public async Task<IActionResult> GetTableQr(Guid id)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var table = await _context.DiningTables
                .IgnoreQueryFilters()
                .Include(t => t.Restaurant)
                .FirstOrDefaultAsync(t => t.Id == id && t.RestaurantId == restaurant!.Id);

            if (table == null) return NotFound();

            var baseUrl = NetworkHelper.ResolveBaseUrl(Request, _configuration);
            var customerUrl = $"{baseUrl}/r/{table.Restaurant!.Slug}/t/{table.Token}";
            var qrDataUri = _qrService.GenerateQrCodeDataUri(customerUrl);

            return Json(new
            {
                tableNumber = table.TableNumber,
                restaurantName = table.Restaurant.Name,
                customerUrl = customerUrl,
                qrDataUri = qrDataUri
            });
        }

        [HttpGet]
        [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> Menu()
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var categories = await _context.Categories
                .IgnoreQueryFilters()
                .Where(c => c.RestaurantId == restaurant!.Id)
                .OrderBy(c => c.DisplayOrder)
                .Include(c => c.MenuItems)
                .ToListAsync();

            ViewBag.Restaurant = restaurant;
            return View(categories);
        }

        [HttpPost]
        public async Task<IActionResult> ToggleItemAvailability(Guid id)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var item = await _context.MenuItems
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.Id == id && m.RestaurantId == restaurant!.Id);

            if (item == null) return NotFound();

            item.IsAvailable = !item.IsAvailable;
            await _context.SaveChangesAsync();

            return Ok(new { success = true, isAvailable = item.IsAvailable });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> AddCategory(string name, int displayOrder)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            if (!string.IsNullOrWhiteSpace(name))
            {
                var category = new Category
                {
                    RestaurantId = restaurant!.Id,
                    Name = name.Trim(),
                    DisplayOrder = displayOrder,
                    IsActive = true
                };
                _context.Categories.Add(category);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Category created successfully.";
            }

            return RedirectToAction(nameof(Menu));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> DeleteCategory(Guid id)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var category = await _context.Categories
                .IgnoreQueryFilters()
                .Include(c => c.MenuItems)
                .FirstOrDefaultAsync(c => c.Id == id && c.RestaurantId == restaurant!.Id);

            if (category == null)
            {
                TempData["Error"] = "Category not found.";
                return RedirectToAction(nameof(Menu));
            }

            if (category.MenuItems.Any())
            {
                TempData["Error"] = $"Cannot delete category '{category.Name}' because it contains {category.MenuItems.Count} items. Please delete or move items first.";
                return RedirectToAction(nameof(Menu));
            }

            _context.Categories.Remove(category);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Category '{category.Name}' deleted successfully.";

            return RedirectToAction(nameof(Menu));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> AddMenuItem(
            Guid categoryId,
            string name,
            string? description,
            decimal price,
            bool isVeg,
            IFormFile? imageFile,
            string? imageUrl)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var category = await _context.Categories
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == categoryId && c.RestaurantId == restaurant!.Id);
            if (category == null)
            {
                TempData["Error"] = "Selected category does not exist or does not belong to this restaurant.";
                return RedirectToAction(nameof(Menu));
            }

            if (string.IsNullOrWhiteSpace(name) || price < 0)
            {
                TempData["Error"] = "Invalid menu item details. Name and a valid price are required.";
                return RedirectToAction(nameof(Menu));
            }

            string? finalImageUrl = null;

            if (imageFile != null && imageFile.Length > 0)
            {
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
                var ext = Path.GetExtension(imageFile.FileName).ToLowerInvariant();
                if (!allowedExtensions.Contains(ext))
                {
                    TempData["Error"] = "Invalid image format. Supported formats: JPG, PNG, WEBP.";
                    return RedirectToAction(nameof(Menu));
                }

                if (imageFile.Length > 10 * 1024 * 1024)
                {
                    TempData["Error"] = "Image file is too large (Maximum allowed size is 10MB).";
                    return RedirectToAction(nameof(Menu));
                }

                using (var checkStream = imageFile.OpenReadStream())
                {
                    if (!NetworkHelper.IsValidImageMagicBytes(checkStream))
                    {
                        TempData["Error"] = "Invalid image file content.";
                        return RedirectToAction(nameof(Menu));
                    }
                }

                var uploadFolder = Path.Combine(_env.WebRootPath, "uploads", "menu-items");
                if (!Directory.Exists(uploadFolder))
                {
                    Directory.CreateDirectory(uploadFolder);
                }

                var fileName = $"{Guid.NewGuid():N}{ext}";
                var filePath = Path.Combine(uploadFolder, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await imageFile.CopyToAsync(stream);
                }

                finalImageUrl = $"/uploads/menu-items/{fileName}";
            }
            else if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                finalImageUrl = imageUrl.Trim();
            }

            var item = new MenuItem
            {
                RestaurantId = restaurant!.Id,
                CategoryId = categoryId,
                Name = name.Trim(),
                Description = description?.Trim(),
                Price = price,
                ImageUrl = finalImageUrl,
                IsVeg = isVeg,
                IsAvailable = true
            };

            _context.MenuItems.Add(item);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Item '{item.Name}' added to menu successfully.";

            return RedirectToAction(nameof(Menu));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> DeleteMenuItem(Guid id)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var item = await _context.MenuItems
                .IgnoreQueryFilters()
                .Include(m => m.Customizations)
                .FirstOrDefaultAsync(m => m.Id == id && m.RestaurantId == restaurant!.Id);

            if (item == null)
            {
                TempData["Error"] = "Menu item not found.";
                return RedirectToAction(nameof(Menu));
            }

            try
            {
                // Check if this item is referenced in any past customer orders
                var hasOrders = await _context.OrderItems
                    .AnyAsync(oi => oi.MenuItemId == id);

                if (hasOrders)
                {
                    // Soft-deactivate to protect financial reports and historic order records
                    item.IsAvailable = false;
                    await _context.SaveChangesAsync();
                    TempData["Success"] = $"Item '{item.Name}' has past order records in sales reports. It has been deactivated (hidden from menu) to preserve revenue history.";
                    return RedirectToAction(nameof(Menu));
                }

                // If no historic orders exist, safe to permanently delete
                if (item.Customizations.Any())
                {
                    _context.ItemCustomizations.RemoveRange(item.Customizations);
                }

                if (!string.IsNullOrWhiteSpace(item.ImageUrl) && item.ImageUrl.StartsWith("/uploads/menu-items/"))
                {
                    try
                    {
                        var oldPath = Path.Combine(_env.WebRootPath, item.ImageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                        if (System.IO.File.Exists(oldPath))
                        {
                            System.IO.File.Delete(oldPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not delete old menu item image file {Url}", item.ImageUrl);
                    }
                }

                _context.MenuItems.Remove(item);
                await _context.SaveChangesAsync();
                TempData["Success"] = $"Item '{item.Name}' deleted successfully.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete menu item {ItemId}", id);
                _context.ChangeTracker.Clear();
                var itemToDeactivate = await _context.MenuItems.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == id);
                if (itemToDeactivate != null)
                {
                    itemToDeactivate.IsAvailable = false;
                    await _context.SaveChangesAsync();
                    TempData["Success"] = $"Item '{itemToDeactivate.Name}' was marked unavailable to preserve existing order references.";
                }
            }

            return RedirectToAction(nameof(Menu));
        }

        [HttpGet]
        public async Task<IActionResult> PrintTableStands([FromQuery] Guid? tableId = null)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var allTables = await _context.DiningTables
                .IgnoreQueryFilters()
                .Where(t => t.RestaurantId == restaurant!.Id && t.IsActive)
                .OrderBy(t => t.TableNumber)
                .ToListAsync();

            var targetTables = tableId.HasValue
                ? allTables.Where(t => t.Id == tableId.Value).ToList()
                : allTables;

            var baseUrl = NetworkHelper.ResolveBaseUrl(Request, _configuration);

            var stands = targetTables.Select(t => new TableStandItemDto
            {
                TableId = t.Id,
                TableNumber = t.TableNumber,
                Capacity = t.Capacity,
                Token = t.Token,
                CustomerUrl = $"{baseUrl}/r/{restaurant!.Slug}/t/{t.Token}",
                QrDataUri = _qrService.GenerateQrCodeDataUri($"{baseUrl}/r/{restaurant!.Slug}/t/{t.Token}")
            }).ToList();

            ViewBag.Restaurant = restaurant;
            ViewBag.AllTables = allTables;
            ViewBag.SelectedTableId = tableId;
            ViewBag.Host = baseUrl;
            return View(stands);
        }

        [HttpGet]
        public async Task<IActionResult> DownloadTableQrPng(Guid id)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var table = await _context.DiningTables
                .IgnoreQueryFilters()
                .Include(t => t.Restaurant)
                .FirstOrDefaultAsync(t => t.Id == id && t.RestaurantId == restaurant!.Id);

            if (table == null || table.Restaurant == null) return NotFound("Table not found.");

            var baseUrl = NetworkHelper.ResolveBaseUrl(Request, _configuration);
            var customerUrl = $"{baseUrl}/r/{table.Restaurant.Slug}/t/{table.Token}";

            var pngBytes = _qrService.GenerateQrCodePngBytes(customerUrl);
            var cleanTable = string.Join("_", table.TableNumber.Split(Path.GetInvalidFileNameChars()));
            var fileName = $"{table.Restaurant.Slug}_{cleanTable}_QR.png";
            return File(pngBytes, "image/png", fileName);
        }

        [HttpGet]
        [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> Feedback()
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var rawFeedbacks = await _context.CustomerFeedbacks
                .IgnoreQueryFilters()
                .Include(f => f.Table)
                .Include(f => f.Order)
                .Where(f => f.RestaurantId == restaurant!.Id)
                .OrderByDescending(f => f.CreatedAtUtc)
                .ToListAsync();

            var tagFrequencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var feedbackDtos = rawFeedbacks.Select(f =>
            {
                var tagList = !string.IsNullOrWhiteSpace(f.Tags)
                    ? f.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                    : new List<string>();

                foreach (var tag in tagList)
                {
                    tagFrequencies[tag] = tagFrequencies.GetValueOrDefault(tag, 0) + 1;
                }

                return new CustomerFeedbackItemDto
                {
                    Id = f.Id,
                    OrderNumber = f.Order?.OrderNumber ?? "N/A",
                    TableNumber = f.Table?.TableNumber ?? "Unknown",
                    OverallRating = f.OverallRating,
                    FoodRating = f.FoodRating,
                    ServiceRating = f.ServiceRating,
                    CustomerName = string.IsNullOrWhiteSpace(f.CustomerName) ? "Guest" : f.CustomerName,
                    Tags = tagList,
                    Comment = f.Comment,
                    CreatedAtUtc = f.CreatedAtUtc,
                    TimeAgo = GetTimeAgo(f.CreatedAtUtc)
                };
            }).ToList();

            var topTags = tagFrequencies.OrderByDescending(kv => kv.Value).Take(8).ToList();

            var vm = new AdminFeedbackViewModel
            {
                Restaurant = restaurant!,
                Feedbacks = feedbackDtos,
                TopTags = topTags
            };

            return View(vm);
        }

        [HttpGet]
        [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> Staff()
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var users = await _context.Users
                .Where(u => u.RestaurantId == restaurant!.Id)
                .OrderByDescending(u => u.CreatedAtUtc)
                .ToListAsync();

            var staffDtos = new List<StaffUserDto>();
            foreach (var user in users)
            {
                var roles = await _userManager.GetRolesAsync(user);
                staffDtos.Add(new StaffUserDto
                {
                    Id = user.Id,
                    FullName = user.FullName,
                    Email = user.Email ?? string.Empty,
                    Role = roles.FirstOrDefault() ?? "Staff",
                    IsActive = user.IsActive,
                    CreatedAtUtc = user.CreatedAtUtc
                });
            }

            ViewBag.Restaurant = restaurant;
            return View(staffDtos);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> AddStaff(string fullName, string email, string role, string password)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                TempData["Error"] = "Full name, email address, and password are required.";
                return RedirectToAction(nameof(Staff));
            }

            var allowedRoles = new[] { AppRoles.Cashier, AppRoles.KitchenStaff, AppRoles.RestaurantAdmin };
            if (!allowedRoles.Contains(role))
            {
                TempData["Error"] = "Invalid staff role selected. Supported roles: Cashier, Kitchen Staff, or Restaurant Admin.";
                return RedirectToAction(nameof(Staff));
            }

            var existingUser = await _userManager.FindByEmailAsync(email.Trim());
            if (existingUser != null)
            {
                TempData["Error"] = $"An account with email '{email.Trim()}' already exists.";
                return RedirectToAction(nameof(Staff));
            }

            var staffUser = new ApplicationUser
            {
                UserName = email.Trim(),
                Email = email.Trim(),
                FullName = fullName.Trim(),
                RestaurantId = restaurant!.Id,
                EmailConfirmed = true,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            };

            var result = await _userManager.CreateAsync(staffUser, password);
            if (!result.Succeeded)
            {
                TempData["Error"] = string.Join("; ", result.Errors.Select(e => e.Description));
                return RedirectToAction(nameof(Staff));
            }

            await _userManager.AddToRoleAsync(staffUser, role);
            await _userManager.AddClaimAsync(staffUser, new Claim("RestaurantId", restaurant!.Id.ToString()));

            TempData["Success"] = $"Staff member '{staffUser.FullName}' ({role}) registered successfully for {restaurant.Name}.";
            return RedirectToAction(nameof(Staff));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> ToggleStaffStatus(string id)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var user = await _userManager.FindByIdAsync(id);
            if (user == null || user.RestaurantId != restaurant!.Id)
            {
                TempData["Error"] = "Staff member not found.";
                return RedirectToAction(nameof(Staff));
            }

            if (user.UserName == User.Identity?.Name)
            {
                TempData["Error"] = "You cannot deactivate your own active session account.";
                return RedirectToAction(nameof(Staff));
            }

            user.IsActive = !user.IsActive;
            await _userManager.UpdateSecurityStampAsync(user);
            await _userManager.UpdateAsync(user);

            TempData["Success"] = $"Staff member '{user.FullName}' status changed to {(user.IsActive ? "Active" : "Inactive")}.";
            return RedirectToAction(nameof(Staff));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> DeleteStaff(string id)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var user = await _userManager.FindByIdAsync(id);
            if (user == null || user.RestaurantId != restaurant!.Id)
            {
                TempData["Error"] = "Staff member not found.";
                return RedirectToAction(nameof(Staff));
            }

            if (user.UserName == User.Identity?.Name)
            {
                TempData["Error"] = "You cannot delete your own account.";
                return RedirectToAction(nameof(Staff));
            }

            await _userManager.UpdateSecurityStampAsync(user);
            var result = await _userManager.DeleteAsync(user);
            if (result.Succeeded)
            {
                TempData["Success"] = $"Staff member '{user.FullName}' removed successfully.";
            }
            else
            {
                TempData["Error"] = "Failed to remove staff member.";
            }

            return RedirectToAction(nameof(Staff));
        }

        private static string GetTimeAgo(DateTime utcTime)
        {
            var span = DateTime.UtcNow - utcTime;
            if (span.TotalMinutes < 1) return "Just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
            return $"{(int)span.TotalDays}d ago";
        }
    }

    public class TableStandItemDto
    {
        public Guid TableId { get; set; }
        public string TableNumber { get; set; } = string.Empty;
        public int Capacity { get; set; }
        public string Token { get; set; } = string.Empty;
        public string CustomerUrl { get; set; } = string.Empty;
        public string QrDataUri { get; set; } = string.Empty;
    }

    public class StaffUserDto
    {
        public string Id { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
