using MenuScan.Data;
using MenuScan.Models.Entities;
using MenuScan.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MenuScan.Controllers
{
    [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
    public class ReportsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ITenantService _tenantService;

        public ReportsController(ApplicationDbContext context, ITenantService tenantService)
        {
            _context = context;
            _tenantService = tenantService;
        }

        [HttpGet]
        public async Task<IActionResult> DailySales()
        {
            var restaurantId = _tenantService.CurrentRestaurantId;
            if (!restaurantId.HasValue)
            {
                var def = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.IsActive);
                if (def == null) return NotFound();
                restaurantId = def.Id;
            }

            var restaurant = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == restaurantId.Value);
            var todayUtc = DateTime.UtcNow.Date;

            // Today's paid bills
            var bills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurantId.Value && b.Status == BillStatus.Paid && b.CreatedAtUtc >= todayUtc)
                .Include(b => b.Payments)
                .Include(b => b.Order)
                    .ThenInclude(o => o!.OrderItems)
                .ToListAsync();

            var totalSales = bills.Sum(b => b.GrandTotal);
            var totalTax = bills.Sum(b => b.TaxAmount);
            var totalBills = bills.Count;
            var avgOrderValue = totalBills > 0 ? totalSales / totalBills : 0;

            // Method breakdown
            var payments = bills.SelectMany(b => b.Payments).ToList();
            var upiTotal = payments.Where(p => p.Method == PaymentMethod.UPI).Sum(p => p.Amount);
            var cashTotal = payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
            var cardTotal = payments.Where(p => p.Method == PaymentMethod.Card).Sum(p => p.Amount);

            // Item popularity
            var itemPopularity = bills.SelectMany(b => b.Order?.OrderItems ?? Enumerable.Empty<OrderItem>())
                .GroupBy(i => i.ItemNameSnapshot)
                .Select(g => new { ItemName = g.Key, Quantity = g.Sum(x => x.Quantity), Revenue = g.Sum(x => x.TotalPrice) })
                .OrderByDescending(x => x.Quantity)
                .Take(10)
                .ToList();

            ViewBag.Restaurant = restaurant;
            ViewBag.TotalSales = totalSales;
            ViewBag.TotalTax = totalTax;
            ViewBag.TotalBills = totalBills;
            ViewBag.AvgOrderValue = avgOrderValue;
            ViewBag.UpiTotal = upiTotal;
            ViewBag.CashTotal = cashTotal;
            ViewBag.CardTotal = cardTotal;
            ViewBag.ItemPopularity = itemPopularity;

            return View(bills);
        }
    }
}
