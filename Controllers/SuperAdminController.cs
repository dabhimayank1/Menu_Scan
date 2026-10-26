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

        public SuperAdminController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            ITenantService tenantService,
            ILogger<SuperAdminController> logger)
        {
            _context = context;
            _userManager = userManager;
            _tenantService = tenantService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var restaurants = await _context.Restaurants
                .IgnoreQueryFilters()
                .Include(r => r.Tables)
                .Include(r => r.MenuItems)
                .Include(r => r.Orders)
                .OrderByDescending(r => r.CreatedAtUtc)
                .ToListAsync();

            var totalOrders = await _context.Orders.IgnoreQueryFilters().CountAsync();
            var totalRevenue = await _context.Bills.IgnoreQueryFilters()
                .Where(b => b.Status == BillStatus.Paid)
                .SumAsync(b => b.GrandTotal);

            var todayUtc = DateTime.UtcNow.Date;
            var todayRevenue = await _context.Bills.IgnoreQueryFilters()
                .Where(b => b.Status == BillStatus.Paid && b.CreatedAtUtc >= todayUtc)
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
            if (!ModelState.IsValid)
            {
                return View(dto);
            }

            // Slug uniqueness check
            var slug = string.IsNullOrWhiteSpace(dto.Slug)
                ? dto.Name.ToLower().Replace(" ", "-").Replace("&", "and")
                : dto.Slug.ToLower().Trim();

            var slugExists = await _context.Restaurants.IgnoreQueryFilters().AnyAsync(r => r.Slug == slug);
            if (slugExists)
            {
                ModelState.AddModelError("Slug", "This URL slug is already taken. Please choose another.");
                return View(dto);
            }

            // Check if admin email is already used
            var existingUser = await _userManager.FindByEmailAsync(dto.AdminEmail);
            if (existingUser != null)
            {
                ModelState.AddModelError("AdminEmail", "An account with this email address already exists.");
                return View(dto);
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // 1. Create Restaurant
                var restaurant = new Restaurant
                {
                    Name = dto.Name.Trim(),
                    Slug = slug,
                    Address = dto.Address?.Trim(),
                    Phone = dto.Phone?.Trim(),
                    TaxNumber = dto.TaxNumber?.Trim(),
                    DefaultTaxRate = dto.DefaultTaxRate > 0 ? dto.DefaultTaxRate : 5.0m,
                    LogoUrl = dto.LogoUrl?.Trim() ?? "https://images.unsplash.com/photo-1554118811-1e0d58224f24?auto=format&fit=crop&w=150&q=80",
                    IsActive = true
                };

                _context.Restaurants.Add(restaurant);
                await _context.SaveChangesAsync();

                // 2. Create Restaurant Admin User
                var adminUser = new ApplicationUser
                {
                    UserName = dto.AdminEmail.Trim(),
                    Email = dto.AdminEmail.Trim(),
                    FullName = dto.AdminFullName.Trim(),
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
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 1", Token = $"T1-{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Capacity = 2 },
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 2", Token = $"T2-{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Capacity = 4 },
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 3", Token = $"T3-{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Capacity = 4 },
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 4", Token = $"T4-{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Capacity = 6 }
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
                ModelState.AddModelError(string.Empty, "An unexpected error occurred during restaurant provisioning.");
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

            return Ok(new { success = true, isActive = restaurant.IsActive });
        }
    }

    public class CreateRestaurantDto
    {
        public string Name { get; set; } = string.Empty;
        public string? Slug { get; set; }
        public string? Address { get; set; }
        public string? Phone { get; set; }
        public string? TaxNumber { get; set; }
        public decimal DefaultTaxRate { get; set; } = 5.0m;
        public string? LogoUrl { get; set; }

        public string AdminFullName { get; set; } = string.Empty;
        public string AdminEmail { get; set; } = string.Empty;
        public string AdminPassword { get; set; } = string.Empty;
    }
}
