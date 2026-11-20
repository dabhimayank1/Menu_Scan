using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MenuScan.Data;
using MenuScan.Models.Entities;
using MenuScan.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MenuScan.Controllers
{
    [Authorize(Roles = AppRoles.SuperAdmin)]
    public class SuperAdminController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ITenantService _tenantService;
        private readonly ILogger<SuperAdminController> _logger;
        private readonly ITimeHelper _timeHelper;
        private readonly IWebHostEnvironment _env;

        public SuperAdminController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            ITenantService tenantService,
            ILogger<SuperAdminController> logger,
            ITimeHelper timeHelper,
            IWebHostEnvironment env)
        {
            _context = context;
            _userManager = userManager;
            _tenantService = tenantService;
            _logger = logger;
            _timeHelper = timeHelper;
            _env = env;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var restaurants = await _context.Restaurants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(r => r.Tables)
                .Include(r => r.MenuItems)
                .Include(r => r.Orders)
                .AsSplitQuery()
                .OrderByDescending(r => r.CreatedAtUtc)
                .ToListAsync();

            var (todayStartUtc, todayEndUtc) = _timeHelper.LocalDayRangeUtc(_timeHelper.Today());

            var totalOrders = await _context.Orders.IgnoreQueryFilters()
                .Where(o => o.Status != OrderStatus.Cancelled)
                .CountAsync();

            var totalRevenue = await _context.Bills.IgnoreQueryFilters()
                .Where(b => b.Status == BillStatus.Paid 
                    && b.Status != BillStatus.Cancelled
                    && (b.Order == null || b.Order.Status != OrderStatus.Cancelled))
                .SumAsync(b => b.GrandTotal);

            var todayRevenue = await _context.Bills.IgnoreQueryFilters()
                .Where(b => b.Status == BillStatus.Paid 
                    && b.Status != BillStatus.Cancelled
                    && (b.Order == null || b.Order.Status != OrderStatus.Cancelled)
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) >= todayStartUtc 
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) < todayEndUtc)
                .SumAsync(b => b.GrandTotal);

            ViewBag.TotalRestaurants = restaurants.Count;
            ViewBag.ActiveRestaurants = restaurants.Count(r => r.IsActive);
            ViewBag.TotalOrders = totalOrders;
            ViewBag.TotalRevenue = totalRevenue;
            ViewBag.TodayRevenue = todayRevenue;

            return View(restaurants);
        }

        [HttpGet]
        public IActionResult CreateRestaurant()
        {
            return View(new CreateRestaurantDto());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateRestaurant(CreateRestaurantDto dto)
        {
            if (dto == null)
            {
                return BadRequest();
            }

            // 1. Basic field normalization
            dto.Name = dto.Name?.Trim() ?? string.Empty;
            dto.AdminFullName = dto.AdminFullName?.Trim() ?? string.Empty;
            dto.AdminEmail = dto.AdminEmail?.Trim() ?? string.Empty;
            dto.Phone = string.IsNullOrWhiteSpace(dto.Phone) ? null : dto.Phone.Trim();
            dto.Address = string.IsNullOrWhiteSpace(dto.Address) ? null : dto.Address.Trim();
            dto.TaxNumber = string.IsNullOrWhiteSpace(dto.TaxNumber) ? null : dto.TaxNumber.Trim();
            dto.LogoUrl = string.IsNullOrWhiteSpace(dto.LogoUrl) ? null : dto.LogoUrl.Trim();

            // 2. Phone format validation
            if (!string.IsNullOrWhiteSpace(dto.Phone) && !SecurityRules.IsValidPhone(dto.Phone))
            {
                ModelState.AddModelError("Phone", "Please enter a valid phone number (7-15 digits, optional + prefix).");
            }

            // 4. Tax rate range check
            if (dto.DefaultTaxRate < 0 || dto.DefaultTaxRate > 100)
            {
                ModelState.AddModelError("DefaultTaxRate", "Default tax rate must be between 0% and 100%.");
            }

            // 5. Password policy and blocklist check
            if (!SecurityRules.ValidatePassword(dto.AdminPassword, out var passwordError))
            {
                ModelState.AddModelError("AdminPassword", passwordError!);
            }

            // 6. Admin email uniqueness check
            var existingUser = await _userManager.FindByEmailAsync(dto.AdminEmail);
            if (existingUser != null)
            {
                ModelState.AddModelError("AdminEmail", "An account with this email address already exists.");
            }

            // 7. Slug handling: auto-generate or validate custom
            string finalSlug;
            bool isAutoGenerated = string.IsNullOrWhiteSpace(dto.Slug);

            if (isAutoGenerated)
            {
                var candidateSlug = SecurityRules.Slugify(dto.Name);
                if (SecurityRules.IsReservedSlug(candidateSlug))
                {
                    candidateSlug = $"{candidateSlug}-shop";
                }

                // Check collision and auto-append suffix -2, -3, etc.
                var baseSlug = candidateSlug;
                var testSlug = baseSlug;
                int suffix = 2;
                while (await _context.Restaurants.IgnoreQueryFilters().AnyAsync(r => r.Slug == testSlug))
                {
                    var suffixStr = $"-{suffix}";
                    var maxBaseLen = 60 - suffixStr.Length;
                    var truncated = baseSlug.Length > maxBaseLen ? baseSlug[..maxBaseLen].TrimEnd('-') : baseSlug;
                    testSlug = $"{truncated}{suffixStr}";
                    suffix++;
                }
                finalSlug = testSlug;
            }
            else
            {
                var rawSlug = dto.Slug!.Trim().ToLowerInvariant();
                if (!SecurityRules.IsValidSlug(rawSlug, minLength: 2, maxLength: 60))
                {
                    ModelState.AddModelError("Slug", "Slug must be 2-60 characters, containing only lowercase letters, numbers, and hyphens (no trailing or consecutive hyphens).");
                }
                else if (SecurityRules.IsReservedSlug(rawSlug))
                {
                    ModelState.AddModelError("Slug", $"'{rawSlug}' is a reserved system keyword and cannot be used as a restaurant URL.");
                }
                else
                {
                    var slugExists = await _context.Restaurants.IgnoreQueryFilters().AnyAsync(r => r.Slug == rawSlug);
                    if (slugExists)
                    {
                        ModelState.AddModelError("Slug", "This URL slug is already taken. Please choose another.");
                    }
                }
                finalSlug = rawSlug;
            }

            if (!ModelState.IsValid)
            {
                return View(dto);
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // 1. Create Restaurant
                var restaurant = new Restaurant
                {
                    Name = dto.Name,
                    Slug = finalSlug,
                    Address = dto.Address,
                    Phone = dto.Phone,
                    TaxNumber = dto.TaxNumber,
                    DefaultTaxRate = dto.DefaultTaxRate,
                    LogoUrl = dto.LogoUrl ?? "https://images.unsplash.com/photo-1554118811-1e0d58224f24?auto=format&fit=crop&w=150&q=80",
                    IsActive = true
                };

                _context.Restaurants.Add(restaurant);
                await _context.SaveChangesAsync();

                // 2. Create Restaurant Admin User
                var adminUser = new ApplicationUser
                {
                    UserName = dto.AdminEmail,
                    Email = dto.AdminEmail,
                    FullName = dto.AdminFullName,
                    RestaurantId = restaurant.Id,
                    EmailConfirmed = true,
                    IsActive = true
                };

                var createResult = await _userManager.CreateAsync(adminUser, dto.AdminPassword);
                if (!createResult.Succeeded)
                {
                    foreach (var error in createResult.Errors)
                    {
                        ModelState.AddModelError(string.Empty, error.Description);
                    }
                    await transaction.RollbackAsync();
                    return View(dto);
                }

                await _userManager.AddToRoleAsync(adminUser, AppRoles.RestaurantAdmin);
                await _userManager.AddClaimAsync(adminUser, new Claim("RestaurantId", restaurant.Id.ToString()));

                // 3. Create Default Tables (4 dining tables)
                var tables = new List<DiningTable>
                {
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 1", Token = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), Capacity = 2 },
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 2", Token = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), Capacity = 4 },
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 3", Token = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), Capacity = 4 },
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 4", Token = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), Capacity = 6 }
                };
                _context.DiningTables.AddRange(tables);

                // 4. Create Initial Starter Category
                var initialCategory = new Category
                {
                    RestaurantId = restaurant.Id,
                    Name = "House Specials",
                    DisplayOrder = 1,
                    IsActive = true
                };
                _context.Categories.Add(initialCategory);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("New restaurant '{Name}' (Slug: {Slug}) provisioned by SuperAdmin.", restaurant.Name, restaurant.Slug);
                TempData["Success"] = $"Restaurant '{restaurant.Name}' provisioned successfully! Admin login: {dto.AdminEmail}";

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to provision restaurant '{Name}'", dto.Name);
                ModelState.AddModelError(string.Empty, "An unexpected error occurred during restaurant provisioning. Please try again.");
                return View(dto);
            }
        }

        [HttpPost]
        public async Task<IActionResult> ToggleRestaurantStatus(Guid id)
        {
            var restaurant = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == id);
            if (restaurant == null) return NotFound();

            restaurant.IsActive = !restaurant.IsActive;
            await _context.SaveChangesAsync();

            // When deactivating a restaurant, invalidate all staff security stamps immediately so active sessions are terminated
            if (!restaurant.IsActive)
            {
                var staffUsers = await _userManager.Users.Where(u => u.RestaurantId == restaurant.Id).ToListAsync();
                foreach (var staff in staffUsers)
                {
                    await _userManager.UpdateSecurityStampAsync(staff);
                }
            }

            return Ok(new { success = true, isActive = restaurant.IsActive });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteRestaurant(Guid id)
        {
            var restaurant = await _context.Restaurants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == id);

            if (restaurant == null)
            {
                TempData["Error"] = "Restaurant not found.";
                return RedirectToAction(nameof(Index));
            }

            var totalRestaurants = await _context.Restaurants.IgnoreQueryFilters().CountAsync();
            if (totalRestaurants <= 1)
            {
                TempData["Error"] = "Cannot delete the only existing restaurant on the platform.";
                return RedirectToAction(nameof(Index));
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // 1. Customer feedbacks
                var feedbacks = await _context.CustomerFeedbacks.IgnoreQueryFilters()
                    .Where(f => f.RestaurantId == id).ToListAsync();
                _context.CustomerFeedbacks.RemoveRange(feedbacks);

                // 2. Service requests
                var serviceRequests = await _context.ServiceRequests.IgnoreQueryFilters()
                    .Where(s => s.RestaurantId == id).ToListAsync();
                _context.ServiceRequests.RemoveRange(serviceRequests);

                // 3. Payments for this restaurant's bills
                var bills = await _context.Bills.IgnoreQueryFilters()
                    .Where(b => b.RestaurantId == id).ToListAsync();
                var billIds = bills.Select(b => b.Id).ToList();

                var payments = await _context.Payments.IgnoreQueryFilters()
                    .Where(p => billIds.Contains(p.BillId)).ToListAsync();
                _context.Payments.RemoveRange(payments);

                // 4. Bills
                _context.Bills.RemoveRange(bills);

                // 5. Orders and related history / items
                var orders = await _context.Orders.IgnoreQueryFilters()
                    .Where(o => o.RestaurantId == id).ToListAsync();
                var orderIds = orders.Select(o => o.Id).ToList();

                var statusHistories = await _context.OrderStatusHistories.IgnoreQueryFilters()
                    .Where(h => orderIds.Contains(h.OrderId)).ToListAsync();
                _context.OrderStatusHistories.RemoveRange(statusHistories);

                var orderItems = await _context.OrderItems.IgnoreQueryFilters()
                    .Where(oi => orderIds.Contains(oi.OrderId)).ToListAsync();
                _context.OrderItems.RemoveRange(orderItems);

                _context.Orders.RemoveRange(orders);

                // 6. Menu items & customizations
                var menuItems = await _context.MenuItems.IgnoreQueryFilters()
                    .Where(m => m.RestaurantId == id).ToListAsync();
                var menuItemIds = menuItems.Select(m => m.Id).ToList();

                var customizations = await _context.ItemCustomizations.IgnoreQueryFilters()
                    .Where(ic => menuItemIds.Contains(ic.MenuItemId)).ToListAsync();
                _context.ItemCustomizations.RemoveRange(customizations);

                _context.MenuItems.RemoveRange(menuItems);

                // 7. Categories
                var categories = await _context.Categories.IgnoreQueryFilters()
                    .Where(c => c.RestaurantId == id).ToListAsync();
                _context.Categories.RemoveRange(categories);

                // 8. Dining tables
                var tables = await _context.DiningTables.IgnoreQueryFilters()
                    .Where(t => t.RestaurantId == id).ToListAsync();
                _context.DiningTables.RemoveRange(tables);

                // 9. Staff accounts
                var staffUsers = await _userManager.Users
                    .Where(u => u.RestaurantId == id).ToListAsync();
                foreach (var staff in staffUsers)
                {
                    await _userManager.DeleteAsync(staff);
                }

                // 10. Clean up physical files if any
                if (!string.IsNullOrWhiteSpace(restaurant.PaymentQrImageUrl))
                {
                    try
                    {
                        var relative = restaurant.PaymentQrImageUrl.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
                        var oldPath = Path.Combine(_env.WebRootPath, relative);
                        if (System.IO.File.Exists(oldPath))
                        {
                            System.IO.File.Delete(oldPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not delete payment QR image for deleted restaurant {Id}", id);
                    }
                }

                // 11. Remove Restaurant itself
                _context.Restaurants.Remove(restaurant);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                // 12. If SuperAdmin currently had this restaurant selected, clear selection cookie
                if (_tenantService.CurrentRestaurantId == id)
                {
                    Response.Cookies.Delete("SelectedRestaurantId");
                }

                _logger.LogInformation("Restaurant '{Name}' ({Id}) and all associated records permanently deleted by SuperAdmin.", restaurant.Name, id);
                TempData["Success"] = $"Restaurant '{restaurant.Name}' has been permanently deleted from the platform.";
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to delete restaurant '{Name}' ({Id})", restaurant.Name, id);
                TempData["Error"] = $"Failed to delete restaurant '{restaurant.Name}': {ex.Message}";
            }

            return RedirectToAction(nameof(Index));
        }
    }

    public class CreateRestaurantDto
    {
        [Required(ErrorMessage = "Restaurant name is required.")]
        [StringLength(100, MinimumLength = 2, ErrorMessage = "Restaurant name must be between 2 and 100 characters.")]
        public string Name { get; set; } = string.Empty;

        [StringLength(60, ErrorMessage = "URL slug cannot exceed 60 characters.")]
        public string? Slug { get; set; }

        [StringLength(250, ErrorMessage = "Address cannot exceed 250 characters.")]
        public string? Address { get; set; }

        [StringLength(20, ErrorMessage = "Phone number cannot exceed 20 characters.")]
        public string? Phone { get; set; }

        [StringLength(50, ErrorMessage = "Tax number cannot exceed 50 characters.")]
        public string? TaxNumber { get; set; }

        [Range(0.0, 100.0, ErrorMessage = "Default tax rate must be between 0% and 100%.")]
        public decimal DefaultTaxRate { get; set; } = 5.0m;

        [StringLength(500, ErrorMessage = "Logo URL cannot exceed 500 characters.")]
        public string? LogoUrl { get; set; }

        [Required(ErrorMessage = "Manager full name is required.")]
        [StringLength(100, MinimumLength = 2, ErrorMessage = "Full name must be between 2 and 100 characters.")]
        public string AdminFullName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Admin login email is required.")]
        [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
        [StringLength(150, ErrorMessage = "Email cannot exceed 150 characters.")]
        public string AdminEmail { get; set; } = string.Empty;

        [Required(ErrorMessage = "Initial password is required.")]
        public string AdminPassword { get; set; } = string.Empty;
    }
}
