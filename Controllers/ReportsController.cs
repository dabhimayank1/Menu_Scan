using MenuScan.Data;
using MenuScan.Models.Entities;
using MenuScan.Models.ViewModels;
using MenuScan.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MenuScan.Controllers
{
    [Authorize(Roles = $"{AppRoles.Cashier},{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
    public class ReportsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ITenantService _tenantService;

        public ReportsController(ApplicationDbContext context, ITenantService tenantService)
        {
            _context = context;
            _tenantService = tenantService;
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
            if (r == null) return (null, NotFound("Restaurant not found."));
            return (r, null);
        }

        // ==========================================
        // 1. DAILY SALES REPORT
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> DailySales(DateTime? date = null)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var targetDate = date?.Date ?? DateTime.UtcNow.AddHours(5.5).Date;
            var startUtc = targetDate.AddHours(-5.5);
            var endUtc = startUtc.AddDays(1);

            if (!date.HasValue)
            {
                // Check if today (IST) has bills; if not, check if any recent bills exist and default to latest active date
                var hasToday = await _context.Bills.IgnoreQueryFilters()
                    .AnyAsync(b => b.RestaurantId == restaurant!.Id && b.Status == BillStatus.Paid && b.CreatedAtUtc >= startUtc && b.CreatedAtUtc < endUtc);
                if (!hasToday)
                {
                    var latestBill = await _context.Bills.IgnoreQueryFilters()
                        .Where(b => b.RestaurantId == restaurant!.Id && b.Status == BillStatus.Paid)
                        .OrderByDescending(b => b.CreatedAtUtc)
                        .FirstOrDefaultAsync();
                    if (latestBill != null)
                    {
                        targetDate = latestBill.CreatedAtUtc.AddHours(5.5).Date;
                        startUtc = targetDate.AddHours(-5.5);
                        endUtc = startUtc.AddDays(1);
                    }
                }
            }

            // Bills for selected IST date
            var bills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant!.Id && b.Status == BillStatus.Paid && b.CreatedAtUtc >= startUtc && b.CreatedAtUtc < endUtc)
                .Include(b => b.Payments)
                .Include(b => b.Order)
                    .ThenInclude(o => o!.Table)
                .Include(b => b.Order)
                    .ThenInclude(o => o!.OrderItems)
                .OrderByDescending(b => b.CreatedAtUtc)
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

            // Hourly trend calculation (8 AM to 11 PM)
            var hourlyLabels = new List<string>();
            var hourlyData = new List<decimal>();
            for (int h = 8; h <= 23; h++)
            {
                hourlyLabels.Add(DateTime.Today.AddHours(h).ToString("h tt"));
                var hourSales = bills
                    .Where(b => b.CreatedAtUtc.AddHours(5.5).Hour == h)
                    .Sum(b => b.GrandTotal);
                hourlyData.Add(hourSales);
            }

            // Cashier breakdown for selected date
            var cashierBreakdowns = bills
                .SelectMany(b => b.Payments.DefaultIfEmpty(), (b, p) => new { Bill = b, Payment = p })
                .GroupBy(x => x.Payment?.RecordedBy ?? "Customer / Online")
                .Select(g => new CashierSalesSummaryDto
                {
                    CashierName = g.Key,
                    BillsCount = g.Select(x => x.Bill.Id).Distinct().Count(),
                    CashCollected = g.Where(x => x.Payment != null && x.Payment.Method == PaymentMethod.Cash).Sum(x => x.Payment!.Amount),
                    UpiCollected = g.Where(x => x.Payment != null && x.Payment.Method == PaymentMethod.UPI).Sum(x => x.Payment!.Amount),
                    CardCollected = g.Where(x => x.Payment != null && x.Payment.Method == PaymentMethod.Card).Sum(x => x.Payment!.Amount),
                    TotalRevenue = g.Where(x => x.Payment != null).Sum(x => x.Payment!.Amount) > 0
                        ? g.Where(x => x.Payment != null).Sum(x => x.Payment!.Amount)
                        : g.Select(x => x.Bill).Distinct().Sum(x => x.GrandTotal),
                    PercentageOfTotal = totalSales > 0 ? (double)(g.Select(x => x.Bill).Distinct().Sum(x => x.GrandTotal) / totalSales) * 100 : 0
                })
                .OrderByDescending(x => x.TotalRevenue)
                .ToList();

            ViewBag.Restaurant = restaurant;
            ViewBag.SelectedDate = targetDate;
            ViewBag.TotalSales = totalSales;
            ViewBag.TotalTax = totalTax;
            ViewBag.TotalBills = totalBills;
            ViewBag.AvgOrderValue = avgOrderValue;
            ViewBag.UpiTotal = upiTotal;
            ViewBag.CashTotal = cashTotal;
            ViewBag.CardTotal = cardTotal;
            ViewBag.ItemPopularity = itemPopularity;
            ViewBag.HourlyLabels = hourlyLabels;
            ViewBag.HourlyData = hourlyData;
            ViewBag.CashierBreakdown = cashierBreakdowns;

            return View(bills);
        }

        // ==========================================
        // 2. MONTHLY SALES REPORT
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> MonthlySales(int? year, int? month)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var now = DateTime.UtcNow.AddHours(5.5);
            var targetYear = year ?? now.Year;
            var targetMonth = month ?? now.Month;

            if (!year.HasValue && !month.HasValue)
            {
                // If current month (IST) has 0 bills, check if there's any latest bill in DB
                var hasThisMonth = await _context.Bills.IgnoreQueryFilters()
                    .AnyAsync(b => b.RestaurantId == restaurant!.Id && b.Status == BillStatus.Paid && b.CreatedAtUtc.AddHours(5.5).Year == now.Year && b.CreatedAtUtc.AddHours(5.5).Month == now.Month);
                if (!hasThisMonth)
                {
                    var latestBill = await _context.Bills.IgnoreQueryFilters()
                        .Where(b => b.RestaurantId == restaurant!.Id && b.Status == BillStatus.Paid)
                        .OrderByDescending(b => b.CreatedAtUtc)
                        .FirstOrDefaultAsync();
                    if (latestBill != null)
                    {
                        targetYear = latestBill.CreatedAtUtc.AddHours(5.5).Year;
                        targetMonth = latestBill.CreatedAtUtc.AddHours(5.5).Month;
                    }
                }
            }

            var startOfMonthUtc = new DateTime(targetYear, targetMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(-5.5);
            var endOfMonthUtc = new DateTime(targetYear, targetMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).AddHours(-5.5);

            var bills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant!.Id && b.Status == BillStatus.Paid && b.CreatedAtUtc >= startOfMonthUtc && b.CreatedAtUtc < endOfMonthUtc)
                .Include(b => b.Payments)
                .Include(b => b.Order)
                    .ThenInclude(o => o!.OrderItems)
                        .ThenInclude(oi => oi.MenuItem)
                            .ThenInclude(mi => mi!.Category)
                .OrderBy(b => b.CreatedAtUtc)
                .ToListAsync();

            var totalSales = bills.Sum(b => b.GrandTotal);
            var totalTax = bills.Sum(b => b.TaxAmount);
            var totalBills = bills.Count;
            var avgOrderValue = totalBills > 0 ? totalSales / totalBills : 0;

            var payments = bills.SelectMany(b => b.Payments).ToList();
            var upiTotal = payments.Where(p => p.Method == PaymentMethod.UPI).Sum(p => p.Amount);
            var cashTotal = payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
            var cardTotal = payments.Where(p => p.Method == PaymentMethod.Card).Sum(p => p.Amount);

            var daysInMonth = DateTime.DaysInMonth(targetYear, targetMonth);
            var dailyBreakdowns = new List<DailySalesSummaryDto>();
            var dayLabels = new List<string>();
            var daySalesData = new List<decimal>();
            var dayOrdersData = new List<int>();

            decimal highestDaySales = 0;
            DateTime? highestSalesDate = null;
            int activeSalesDays = 0;

            for (int day = 1; day <= daysInMonth; day++)
            {
                var curDate = new DateTime(targetYear, targetMonth, day);
                var dayBills = bills.Where(b => b.CreatedAtUtc.AddHours(5.5).Date == curDate.Date).ToList();
                var daySales = dayBills.Sum(b => b.GrandTotal);
                var dayTax = dayBills.Sum(b => b.TaxAmount);
                var dayPayments = dayBills.SelectMany(b => b.Payments).ToList();
                var dayUpi = dayPayments.Where(p => p.Method == PaymentMethod.UPI).Sum(p => p.Amount);
                var dayCash = dayPayments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
                var dayCard = dayPayments.Where(p => p.Method == PaymentMethod.Card).Sum(p => p.Amount);

                if (dayBills.Count > 0)
                {
                    activeSalesDays++;
                    if (daySales > highestDaySales)
                    {
                        highestDaySales = daySales;
                        highestSalesDate = curDate;
                    }
                }

                dailyBreakdowns.Add(new DailySalesSummaryDto
                {
                    Date = curDate,
                    OrdersCount = dayBills.Count,
                    GrossSales = daySales - dayTax,
                    TaxAmount = dayTax,
                    NetSales = daySales,
                    UpiAmount = dayUpi,
                    CashAmount = dayCash,
                    CardAmount = dayCard
                });

                dayLabels.Add(curDate.ToString("dd MMM"));
                daySalesData.Add(daySales);
                dayOrdersData.Add(dayBills.Count);
            }

            var dailyAverageSales = activeSalesDays > 0 ? totalSales / activeSalesDays : 0;

            // Top selling items
            var allOrderItems = bills.SelectMany(b => b.Order?.OrderItems ?? Enumerable.Empty<OrderItem>()).ToList();
            var totalItemsSold = allOrderItems.Sum(x => x.Quantity);

            var topItems = allOrderItems
                .GroupBy(i => i.ItemNameSnapshot)
                .Select(g => new TopItemDto
                {
                    ItemName = g.Key,
                    Quantity = g.Sum(x => x.Quantity),
                    Revenue = g.Sum(x => x.TotalPrice),
                    PercentageOfTotal = totalItemsSold > 0 ? ((double)g.Sum(x => x.Quantity) / totalItemsSold) * 100 : 0
                })
                .OrderByDescending(x => x.Quantity)
                .Take(10)
                .ToList();

            // Category breakdown
            var totalItemRevenue = allOrderItems.Sum(x => x.TotalPrice);
            var categoryBreakdowns = allOrderItems
                .GroupBy(i => i.MenuItem?.Category?.Name ?? "General / Uncategorized")
                .Select(g => new CategorySalesDto
                {
                    CategoryName = g.Key,
                    Quantity = g.Sum(x => x.Quantity),
                    Revenue = g.Sum(x => x.TotalPrice),
                    PercentageOfTotal = totalItemRevenue > 0 ? (double)(g.Sum(x => x.TotalPrice) / totalItemRevenue) * 100 : 0
                })
                .OrderByDescending(x => x.Revenue)
                .ToList();

            // Cashier / Staff Settlement Breakdown
            var cashierBreakdowns = bills
                .SelectMany(b => b.Payments.DefaultIfEmpty(), (b, p) => new { Bill = b, Payment = p })
                .GroupBy(x => x.Payment?.RecordedBy ?? "Customer / Online")
                .Select(g => new CashierSalesSummaryDto
                {
                    CashierName = g.Key,
                    BillsCount = g.Select(x => x.Bill.Id).Distinct().Count(),
                    CashCollected = g.Where(x => x.Payment != null && x.Payment.Method == PaymentMethod.Cash).Sum(x => x.Payment!.Amount),
                    UpiCollected = g.Where(x => x.Payment != null && x.Payment.Method == PaymentMethod.UPI).Sum(x => x.Payment!.Amount),
                    CardCollected = g.Where(x => x.Payment != null && x.Payment.Method == PaymentMethod.Card).Sum(x => x.Payment!.Amount),
                    TotalRevenue = g.Where(x => x.Payment != null).Sum(x => x.Payment!.Amount) > 0
                        ? g.Where(x => x.Payment != null).Sum(x => x.Payment!.Amount)
                        : g.Select(x => x.Bill).Distinct().Sum(x => x.GrandTotal),
                    PercentageOfTotal = totalSales > 0 ? (double)(g.Select(x => x.Bill).Distinct().Sum(x => x.GrandTotal) / totalSales) * 100 : 0
                })
                .OrderByDescending(x => x.TotalRevenue)
                .ToList();

            var vm = new MonthlySalesViewModel
            {
                Restaurant = restaurant,
                Year = targetYear,
                Month = targetMonth,
                TotalSales = totalSales,
                TotalTax = totalTax,
                TotalBills = totalBills,
                AvgOrderValue = avgOrderValue,
                DailyAverageSales = dailyAverageSales,
                ActiveSalesDays = activeSalesDays,
                HighestDaySales = highestDaySales,
                HighestSalesDate = highestSalesDate,
                UpiTotal = upiTotal,
                CashTotal = cashTotal,
                CardTotal = cardTotal,
                DailyBreakdowns = dailyBreakdowns,
                TopSellingItems = topItems,
                CategoryBreakdowns = categoryBreakdowns,
                CashierBreakdowns = cashierBreakdowns,
                DayLabels = dayLabels,
                DaySalesData = daySalesData,
                DayOrdersData = dayOrdersData
            };

            return View(vm);
        }

        // ==========================================
        // 3. EXPORT EXCEL & CSV
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> ExportMonthlyExcel(int? year, int? month)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var now = DateTime.UtcNow.AddHours(5.5);
            var targetYear = year ?? now.Year;
            var targetMonth = month ?? now.Month;

            var startOfMonthUtc = new DateTime(targetYear, targetMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(-5.5);
            var endOfMonthUtc = new DateTime(targetYear, targetMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).AddHours(-5.5);

            var bills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant!.Id && b.Status == BillStatus.Paid && b.CreatedAtUtc >= startOfMonthUtc && b.CreatedAtUtc < endOfMonthUtc)
                .Include(b => b.Payments)
                .ToListAsync();

            var daysInMonth = DateTime.DaysInMonth(targetYear, targetMonth);
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("<html xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:x=\"urn:schemas-microsoft-com:office:excel\" xmlns=\"http://www.w3.org/TR/REC-html40\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">");
            sb.AppendLine("<!--[if gte mso 9]><xml><x:ExcelWorkbook><x:ExcelWorksheets><x:ExcelWorksheet><x:Name>Monthly Sales</x:Name><x:WorksheetOptions><x:DisplayGridlines/></x:WorksheetOptions></x:ExcelWorksheet></x:ExcelWorksheets></x:ExcelWorkbook></xml><![endif]-->");
            sb.AppendLine("<style>");
            sb.AppendLine("  body { font-family: Calibri, 'Segoe UI', Arial, sans-serif; font-size: 11pt; color: #18120E; }");
            sb.AppendLine("  .report-title { font-size: 16pt; font-weight: bold; color: #18120E; padding-bottom: 4px; }");
            sb.AppendLine("  .report-meta { font-size: 10pt; color: #555555; padding-bottom: 14px; }");
            sb.AppendLine("  table { border-collapse: collapse; }");
            sb.AppendLine("  th { background-color: #18120E; color: #FFFFFF; font-weight: bold; text-align: center; padding: 10px 16px; border: 1px solid #333333; mso-number-format:\"\\@\"; }");
            sb.AppendLine("  td { padding: 8px 14px; border: 1px solid #DDDDDD; vertical-align: middle; }");
            sb.AppendLine("  .date-col { mso-number-format:\"yyyy\\-mm\\-dd\"; text-align: center; }");
            sb.AppendLine("  .day-col { mso-number-format:\"\\@\"; text-align: left; }");
            sb.AppendLine("  .qty-col { mso-number-format:\"#,##0\"; text-align: center; font-weight: bold; }");
            sb.AppendLine("  .num-col { mso-number-format:\"#,##0.00\"; text-align: right; }");
            sb.AppendLine("  .rev-col { mso-number-format:\"#,##0.00\"; text-align: right; font-weight: bold; color: #198754; }");
            sb.AppendLine("  .total-row td { font-weight: bold; background-color: #F6EFE6; border-top: 2px solid #18120E; border-bottom: 3px double #18120E; padding: 10px 14px; font-size: 11pt; }");
            sb.AppendLine("  .weekend td { background-color: #FAFAF7; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            var monthDisplay = new DateTime(targetYear, targetMonth, 1).ToString("MMMM yyyy");
            sb.AppendLine($"<div class=\"report-title\">{System.Net.WebUtility.HtmlEncode(restaurant.Name)} &mdash; Monthly Sales &amp; Settlement Report</div>");
            sb.AppendLine($"<div class=\"report-meta\">Month / Period: <b>{monthDisplay}</b> &bull; Exported On: {DateTime.Now:dd MMM yyyy, hh:mm tt}</div>");

            sb.AppendLine("<table border=\"1\" cellspacing=\"0\" cellpadding=\"6\">");
            sb.AppendLine("<thead>");
            sb.AppendLine("  <tr>");
            sb.AppendLine("    <th style=\"width: 130px;\">Date</th>");
            sb.AppendLine("    <th style=\"width: 130px;\">Day of Week</th>");
            sb.AppendLine("    <th style=\"width: 110px;\">Orders Count</th>");
            sb.AppendLine("    <th style=\"width: 130px;\">GST / Tax (₹)</th>");
            sb.AppendLine("    <th style=\"width: 170px;\">Net Settled Sales (₹)</th>");
            sb.AppendLine("    <th style=\"width: 140px;\">UPI Payment (₹)</th>");
            sb.AppendLine("    <th style=\"width: 140px;\">Cash Counter (₹)</th>");
            sb.AppendLine("    <th style=\"width: 140px;\">Card Payment (₹)</th>");
            sb.AppendLine("  </tr>");
            sb.AppendLine("</thead>");
            sb.AppendLine("<tbody>");

            decimal totalSales = 0;
            decimal totalTax = 0;
            int totalOrders = 0;
            decimal totalUpi = 0;
            decimal totalCash = 0;
            decimal totalCard = 0;

            for (int day = 1; day <= daysInMonth; day++)
            {
                var curDate = new DateTime(targetYear, targetMonth, day);
                var dayBills = bills.Where(b => b.CreatedAtUtc.AddHours(5.5).Date == curDate.Date).ToList();
                var daySales = dayBills.Sum(b => b.GrandTotal);
                var dayTax = dayBills.Sum(b => b.TaxAmount);
                var dayPayments = dayBills.SelectMany(b => b.Payments).ToList();
                var dayUpi = dayPayments.Where(p => p.Method == PaymentMethod.UPI).Sum(p => p.Amount);
                var dayCash = dayPayments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
                var dayCard = dayPayments.Where(p => p.Method == PaymentMethod.Card).Sum(p => p.Amount);

                totalSales += daySales;
                totalTax += dayTax;
                totalOrders += dayBills.Count;
                totalUpi += dayUpi;
                totalCash += dayCash;
                totalCard += dayCard;

                var isWeekend = curDate.DayOfWeek == DayOfWeek.Saturday || curDate.DayOfWeek == DayOfWeek.Sunday;
                var rowClass = isWeekend ? "class=\"weekend\"" : "";

                sb.AppendLine($"  <tr {rowClass}>");
                sb.AppendLine($"    <td class=\"date-col\">{curDate:yyyy-MM-dd}</td>");
                sb.AppendLine($"    <td class=\"day-col\">{curDate:dddd}</td>");
                sb.AppendLine($"    <td class=\"qty-col\">{(dayBills.Count > 0 ? dayBills.Count.ToString() : "-")}</td>");
                sb.AppendLine($"    <td class=\"num-col\">{dayTax:0.00}</td>");
                sb.AppendLine($"    <td class=\"rev-col\">{daySales:0.00}</td>");
                sb.AppendLine($"    <td class=\"num-col\">{dayUpi:0.00}</td>");
                sb.AppendLine($"    <td class=\"num-col\">{dayCash:0.00}</td>");
                sb.AppendLine($"    <td class=\"num-col\">{dayCard:0.00}</td>");
                sb.AppendLine("  </tr>");
            }

            sb.AppendLine("</tbody>");
            sb.AppendLine("<tfoot>");
            sb.AppendLine("  <tr class=\"total-row\">");
            sb.AppendLine("    <td colspan=\"2\" style=\"text-align: left;\">TOTAL / MONTH SUMMARY</td>");
            sb.AppendLine($"    <td class=\"qty-col\">{totalOrders}</td>");
            sb.AppendLine($"    <td class=\"num-col\">{totalTax:0.00}</td>");
            sb.AppendLine($"    <td class=\"rev-col\">{totalSales:0.00}</td>");
            sb.AppendLine($"    <td class=\"num-col\">{totalUpi:0.00}</td>");
            sb.AppendLine($"    <td class=\"num-col\">{totalCash:0.00}</td>");
            sb.AppendLine($"    <td class=\"num-col\">{totalCard:0.00}</td>");
            sb.AppendLine("  </tr>");
            sb.AppendLine("</tfoot>");
            sb.AppendLine("</table>");

            // Cashier settlement section in Excel
            sb.AppendLine("<br/><br/>");
            sb.AppendLine($"<div class=\"report-title\">Cashier &amp; Settlement Breakdown &mdash; {monthDisplay}</div>");
            sb.AppendLine("<table border=\"1\" cellspacing=\"0\" cellpadding=\"6\">");
            sb.AppendLine("<thead>");
            sb.AppendLine("  <tr>");
            sb.AppendLine("    <th style=\"width: 220px;\">Cashier / Source</th>");
            sb.AppendLine("    <th style=\"width: 110px;\">Bills Settled</th>");
            sb.AppendLine("    <th style=\"width: 140px;\">Cash Counter (₹)</th>");
            sb.AppendLine("    <th style=\"width: 140px;\">UPI Collected (₹)</th>");
            sb.AppendLine("    <th style=\"width: 140px;\">Card Collected (₹)</th>");
            sb.AppendLine("    <th style=\"width: 170px;\">Total Revenue Handled (₹)</th>");
            sb.AppendLine("    <th style=\"width: 120px;\">Revenue Share (%)</th>");
            sb.AppendLine("  </tr>");
            sb.AppendLine("</thead>");
            sb.AppendLine("<tbody>");

            var excelCashiers = bills
                .SelectMany(b => b.Payments.DefaultIfEmpty(), (b, p) => new { Bill = b, Payment = p })
                .GroupBy(x => x.Payment?.RecordedBy ?? "Customer / Online")
                .Select(g => new
                {
                    CashierName = g.Key,
                    BillsCount = g.Select(x => x.Bill.Id).Distinct().Count(),
                    CashCollected = g.Where(x => x.Payment != null && x.Payment.Method == PaymentMethod.Cash).Sum(x => x.Payment!.Amount),
                    UpiCollected = g.Where(x => x.Payment != null && x.Payment.Method == PaymentMethod.UPI).Sum(x => x.Payment!.Amount),
                    CardCollected = g.Where(x => x.Payment != null && x.Payment.Method == PaymentMethod.Card).Sum(x => x.Payment!.Amount),
                    TotalRevenue = g.Where(x => x.Payment != null).Sum(x => x.Payment!.Amount) > 0
                        ? g.Where(x => x.Payment != null).Sum(x => x.Payment!.Amount)
                        : g.Select(x => x.Bill).Distinct().Sum(x => x.GrandTotal),
                    PercentageOfTotal = totalSales > 0 ? (double)(g.Select(x => x.Bill).Distinct().Sum(x => x.GrandTotal) / totalSales) * 100 : 0
                })
                .OrderByDescending(x => x.TotalRevenue)
                .ToList();

            foreach (var c in excelCashiers)
            {
                sb.AppendLine("  <tr>");
                sb.AppendLine($"    <td class=\"day-col\"><b>{c.CashierName}</b></td>");
                sb.AppendLine($"    <td class=\"qty-col\">{c.BillsCount}</td>");
                sb.AppendLine($"    <td class=\"num-col\">{c.CashCollected:0.00}</td>");
                sb.AppendLine($"    <td class=\"num-col\">{c.UpiCollected:0.00}</td>");
                sb.AppendLine($"    <td class=\"num-col\">{c.CardCollected:0.00}</td>");
                sb.AppendLine($"    <td class=\"rev-col\">{c.TotalRevenue:0.00}</td>");
                sb.AppendLine($"    <td class=\"qty-col\">{c.PercentageOfTotal:0.1}%</td>");
                sb.AppendLine("  </tr>");
            }

            sb.AppendLine("</tbody>");
            sb.AppendLine("</table>");

            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            var cleanName = System.Text.RegularExpressions.Regex.Replace(restaurant.Name, @"[^a-zA-Z0-9]", "_");
            return File(bytes, "application/vnd.ms-excel", $"Monthly_Sales_{cleanName}_{targetYear}_{targetMonth:D2}.xls");
        }

        [HttpGet]
        public async Task<IActionResult> ExportMonthlyCsv(int? year, int? month)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var now = DateTime.UtcNow.AddHours(5.5);
            var targetYear = year ?? now.Year;
            var targetMonth = month ?? now.Month;

            var startOfMonthUtc = new DateTime(targetYear, targetMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(-5.5);
            var endOfMonthUtc = new DateTime(targetYear, targetMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).AddHours(-5.5);

            var bills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant!.Id && b.Status == BillStatus.Paid && b.CreatedAtUtc >= startOfMonthUtc && b.CreatedAtUtc < endOfMonthUtc)
                .Include(b => b.Payments)
                .ToListAsync();

            var daysInMonth = DateTime.DaysInMonth(targetYear, targetMonth);
            var sb = new System.Text.StringBuilder();

            // Direct clean header so Excel assigns 8 standard columns without clipping
            sb.AppendLine("Date,Day of Week,Orders Count,GST / Tax (INR),Net Settled Revenue (INR),UPI (INR),Cash (INR),Card (INR)");

            decimal totalSales = 0;
            decimal totalTax = 0;
            int totalOrders = 0;
            decimal totalUpi = 0;
            decimal totalCash = 0;
            decimal totalCard = 0;

            for (int day = 1; day <= daysInMonth; day++)
            {
                var curDate = new DateTime(targetYear, targetMonth, day);
                var dayBills = bills.Where(b => b.CreatedAtUtc.AddHours(5.5).Date == curDate.Date).ToList();
                var daySales = dayBills.Sum(b => b.GrandTotal);
                var dayTax = dayBills.Sum(b => b.TaxAmount);
                var dayPayments = dayBills.SelectMany(b => b.Payments).ToList();
                var dayUpi = dayPayments.Where(p => p.Method == PaymentMethod.UPI).Sum(p => p.Amount);
                var dayCash = dayPayments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
                var dayCard = dayPayments.Where(p => p.Method == PaymentMethod.Card).Sum(p => p.Amount);

                totalSales += daySales;
                totalTax += dayTax;
                totalOrders += dayBills.Count;
                totalUpi += dayUpi;
                totalCash += dayCash;
                totalCard += dayCard;

                sb.AppendLine($"{curDate:yyyy-MM-dd},{curDate:dddd},{dayBills.Count},{dayTax:F2},{daySales:F2},{dayUpi:F2},{dayCash:F2},{dayCard:F2}");
            }

            sb.AppendLine($"TOTAL,All Days,{totalOrders},{totalTax:F2},{totalSales:F2},{totalUpi:F2},{totalCash:F2},{totalCard:F2}");

            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            var cleanName = System.Text.RegularExpressions.Regex.Replace(restaurant.Name, @"[^a-zA-Z0-9]", "_");
            return File(bytes, "text/csv; charset=utf-8", $"Monthly_Sales_{cleanName}_{targetYear}_{targetMonth:D2}.csv");
        }

        // ==========================================
        // 4. WHATSAPP APIs
        // ==========================================
        [HttpGet("api/reports/daily-sales-summary")]
        public async Task<IActionResult> GetDailySalesSummary([FromQuery] DateTime? date = null, [FromQuery] string? phone = null)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var targetDate = date?.Date ?? DateTime.UtcNow.Date;
            var nextDay = targetDate.AddDays(1);

            var bills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant!.Id && b.Status == BillStatus.Paid && b.CreatedAtUtc >= targetDate && b.CreatedAtUtc < nextDay)
                .Include(b => b.Payments)
                .Include(b => b.Order)
                    .ThenInclude(o => o!.OrderItems)
                .ToListAsync();

            var totalSales = bills.Sum(b => b.GrandTotal);
            var totalTax = bills.Sum(b => b.TaxAmount);
            var totalBills = bills.Count;
            var avgOrderValue = totalBills > 0 ? totalSales / totalBills : 0;

            var payments = bills.SelectMany(b => b.Payments).ToList();
            var upiTotal = payments.Where(p => p.Method == PaymentMethod.UPI).Sum(p => p.Amount);
            var cashTotal = payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
            var cardTotal = payments.Where(p => p.Method == PaymentMethod.Card).Sum(p => p.Amount);

            var topItems = bills.SelectMany(b => b.Order?.OrderItems ?? Enumerable.Empty<OrderItem>())
                .GroupBy(i => i.ItemNameSnapshot)
                .Select(g => (ItemName: g.Key, Quantity: g.Sum(x => x.Quantity), Revenue: g.Sum(x => x.TotalPrice)))
                .OrderByDescending(x => x.Quantity)
                .Take(5)
                .ToList();

            var targetPhone = !string.IsNullOrWhiteSpace(phone) ? phone.Trim() : restaurant!.Phone;

            var msg = WhatsAppHelper.BuildDailySalesSummaryMessage(
                restaurant!.Name,
                targetDate,
                totalSales,
                totalBills,
                avgOrderValue,
                upiTotal,
                cashTotal,
                cardTotal,
                totalTax,
                topItems
            );

            var whatsAppWebUrl = WhatsAppHelper.BuildWhatsAppUrl(targetPhone, msg, forceWeb: true);
            var whatsAppAppUrl = WhatsAppHelper.BuildWhatsAppUrl(targetPhone, msg, forceWeb: false);

            return Ok(new
            {
                success = true,
                restaurantName = restaurant.Name,
                ownerPhone = targetPhone,
                totalSales,
                totalBills,
                avgOrderValue,
                upiTotal,
                cashTotal,
                cardTotal,
                taxTotal = totalTax,
                summaryText = msg,
                whatsAppWebUrl,
                whatsAppAppUrl
            });
        }

        [HttpGet("api/reports/monthly-sales-summary")]
        public async Task<IActionResult> GetMonthlySalesSummary([FromQuery] int? year, [FromQuery] int? month, [FromQuery] string? phone = null)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var now = DateTime.UtcNow.AddHours(5.5);
            var targetYear = year ?? now.Year;
            var targetMonth = month ?? now.Month;

            var startOfMonthUtc = new DateTime(targetYear, targetMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(-5.5);
            var endOfMonthUtc = new DateTime(targetYear, targetMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).AddHours(-5.5);

            var bills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant!.Id && b.Status == BillStatus.Paid && b.CreatedAtUtc >= startOfMonthUtc && b.CreatedAtUtc < endOfMonthUtc)
                .Include(b => b.Payments)
                .Include(b => b.Order)
                    .ThenInclude(o => o!.OrderItems)
                .ToListAsync();

            var totalSales = bills.Sum(b => b.GrandTotal);
            var totalTax = bills.Sum(b => b.TaxAmount);
            var totalBills = bills.Count;
            var avgOrderValue = totalBills > 0 ? totalSales / totalBills : 0;

            var payments = bills.SelectMany(b => b.Payments).ToList();
            var upiTotal = payments.Where(p => p.Method == PaymentMethod.UPI).Sum(p => p.Amount);
            var cashTotal = payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
            var cardTotal = payments.Where(p => p.Method == PaymentMethod.Card).Sum(p => p.Amount);

            var activeDays = bills.Select(b => b.CreatedAtUtc.ToLocalTime().Date).Distinct().Count();
            var dailyAverage = activeDays > 0 ? totalSales / activeDays : 0;

            var topItems = bills.SelectMany(b => b.Order?.OrderItems ?? Enumerable.Empty<OrderItem>())
                .GroupBy(i => i.ItemNameSnapshot)
                .Select(g => (ItemName: g.Key, Quantity: g.Sum(x => x.Quantity), Revenue: g.Sum(x => x.TotalPrice)))
                .OrderByDescending(x => x.Quantity)
                .Take(5)
                .ToList();

            var targetPhone = !string.IsNullOrWhiteSpace(phone) ? phone.Trim() : restaurant.Phone;

            var msg = WhatsAppHelper.BuildMonthlySalesSummaryMessage(
                restaurant.Name,
                targetYear,
                targetMonth,
                totalSales,
                totalBills,
                avgOrderValue,
                dailyAverage,
                upiTotal,
                cashTotal,
                cardTotal,
                totalTax,
                topItems
            );

            var whatsAppWebUrl = WhatsAppHelper.BuildWhatsAppUrl(targetPhone, msg, forceWeb: true);
            var whatsAppAppUrl = WhatsAppHelper.BuildWhatsAppUrl(targetPhone, msg, forceWeb: false);

            return Ok(new
            {
                success = true,
                restaurantName = restaurant.Name,
                period = new DateTime(targetYear, targetMonth, 1).ToString("MMMM yyyy"),
                ownerPhone = targetPhone,
                totalSales,
                totalBills,
                avgOrderValue,
                dailyAverage,
                upiTotal,
                cashTotal,
                cardTotal,
                taxTotal = totalTax,
                summaryText = msg,
                whatsAppWebUrl,
                whatsAppAppUrl
            });
        }
    }
}
