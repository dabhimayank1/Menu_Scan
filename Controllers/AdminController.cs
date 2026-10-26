using MenuScan.Data;
using MenuScan.Models.Entities;
using MenuScan.Models.ViewModels;
using MenuScan.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MenuScan.Controllers
{
    [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
    public class AdminController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ITenantService _tenantService;
        private readonly IQRService _qrService;
        private readonly ILogger<AdminController> _logger;

        public AdminController(
            ApplicationDbContext context,
            ITenantService tenantService,
            IQRService qrService,
            ILogger<AdminController> logger)
        {
            _context = context;
            _tenantService = tenantService;
            _qrService = qrService;
            _logger = logger;
        }

        private async Task<Restaurant?> GetCurrentRestaurantAsync()
        {
            var restaurantId = _tenantService.CurrentRestaurantId;
            if (restaurantId.HasValue)
            {
                return await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == restaurantId.Value);
            }
            return await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.IsActive);
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var restaurant = await GetCurrentRestaurantAsync();
            if (restaurant == null) return NotFound("Restaurant not configured.");

            var todayUtc = DateTime.UtcNow.Date;
            var todayBills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant.Id && b.Status == BillStatus.Paid && b.CreatedAtUtc >= todayUtc)
                .ToListAsync();

            var todayOrdersCount = await _context.Orders
                .IgnoreQueryFilters()
                .CountAsync(o => o.RestaurantId == restaurant.Id && o.CreatedAtUtc >= todayUtc);

            var tablesCount = await _context.DiningTables
                .IgnoreQueryFilters()
                .CountAsync(t => t.RestaurantId == restaurant.Id);

            var activeItemsCount = await _context.MenuItems
                .IgnoreQueryFilters()
                .CountAsync(m => m.RestaurantId == restaurant.Id && m.IsAvailable);

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
            var restaurant = await GetCurrentRestaurantAsync();
            if (restaurant == null) return NotFound();

            var tables = await _context.DiningTables
                .IgnoreQueryFilters()
                .Where(t => t.RestaurantId == restaurant.Id)
                .OrderBy(t => t.TableNumber)
                .ToListAsync();

            ViewBag.Restaurant = restaurant;
            return View(tables);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddTable(string tableNumber, int capacity)
        {
            var restaurant = await GetCurrentRestaurantAsync();
            if (restaurant == null) return NotFound();

            if (string.IsNullOrWhiteSpace(tableNumber))
            {
                TempData["Error"] = "Table number cannot be empty.";
                return RedirectToAction(nameof(Tables));
            }

            var table = new DiningTable
            {
                RestaurantId = restaurant.Id,
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
        public async Task<IActionResult> ToggleTable(Guid id)
        {
            var table = await _context.DiningTables.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == id);
            if (table == null) return NotFound();

            table.IsActive = !table.IsActive;
            await _context.SaveChangesAsync();

            return Ok(new { success = true, isActive = table.IsActive });
        }

        [HttpGet]
        public async Task<IActionResult> GetTableQr(Guid id)
        {
            var table = await _context.DiningTables
                .IgnoreQueryFilters()
                .Include(t => t.Restaurant)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (table == null) return NotFound();

            var request = HttpContext.Request;
            var host = request.Host.Host == "localhost" ? "192.168.1.8:5073" : request.Host.Value;
            var customerUrl = $"http://{host}/r/{table.Restaurant!.Slug}/t/{table.Token}";
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
        public async Task<IActionResult> Menu()
        {
            var restaurant = await GetCurrentRestaurantAsync();
            if (restaurant == null) return NotFound();

            var categories = await _context.Categories
                .IgnoreQueryFilters()
                .Where(c => c.RestaurantId == restaurant.Id)
                .OrderBy(c => c.DisplayOrder)
                .Include(c => c.MenuItems)
                .ToListAsync();

            ViewBag.Restaurant = restaurant;
            return View(categories);
        }

        [HttpPost]
        public async Task<IActionResult> ToggleItemAvailability(Guid id)
        {
            var item = await _context.MenuItems.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == id);
            if (item == null) return NotFound();

            item.IsAvailable = !item.IsAvailable;
            await _context.SaveChangesAsync();

            return Ok(new { success = true, isAvailable = item.IsAvailable });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddCategory(string name, int displayOrder)
        {
            var restaurant = await GetCurrentRestaurantAsync();
            if (restaurant == null) return NotFound();

            if (!string.IsNullOrWhiteSpace(name))
            {
                var category = new Category
                {
                    RestaurantId = restaurant.Id,
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
        public async Task<IActionResult> AddMenuItem(Guid categoryId, string name, string description, decimal price, bool isVeg)
        {
            var restaurant = await GetCurrentRestaurantAsync();
            if (restaurant == null) return NotFound();

            if (string.IsNullOrWhiteSpace(name) || price < 0)
            {
                TempData["Error"] = "Invalid menu item details.";
                return RedirectToAction(nameof(Menu));
            }

            var item = new MenuItem
            {
                RestaurantId = restaurant.Id,
                CategoryId = categoryId,
                Name = name.Trim(),
                Description = description?.Trim(),
                Price = price,
                IsVeg = isVeg,
                IsAvailable = true
            };

            _context.MenuItems.Add(item);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Item '{item.Name}' added to menu.";

            return RedirectToAction(nameof(Menu));
        }
    }
}
