using ClosedXML.Excel;
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
        private readonly ITimeHelper _timeHelper;

        public ReportsController(ApplicationDbContext context, ITenantService tenantService, ITimeHelper timeHelper)
        {
            _context = context;
            _tenantService = tenantService;
            _timeHelper = timeHelper;
        }

        private static string EscapeCsvCell(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            // Formula injection prevention: if starts with =, +, -, @, prefix with single quote '
            if (value.StartsWith("=") || value.StartsWith("+") || value.StartsWith("-") || value.StartsWith("@"))
            {
                value = "'" + value;
            }

            // CSV escaping: if contains comma, quote, or newline, escape double quotes and wrap in quotes
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\r") || value.Contains("\n"))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }

            return value;
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
            if (!r.IsActive && !User.IsInRole(AppRoles.SuperAdmin))
            {
                return (null, Forbid());
            }
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

            var targetDate = date?.Date ?? _timeHelper.Today();
            var (startUtc, endUtc) = _timeHelper.LocalDayRangeUtc(targetDate);

            if (!date.HasValue)
            {
                // Check if today (IST) has bills; if not, check if any recent bills exist and default to latest active date
                var hasToday = await _context.Bills.IgnoreQueryFilters()
                    .AnyAsync(b => b.RestaurantId == restaurant!.Id 
                        && b.Status == BillStatus.Paid 
                        && b.Status != BillStatus.Cancelled
                        && (b.Order == null || b.Order.Status != OrderStatus.Cancelled)
                        && (b.ClosedAtUtc ?? b.CreatedAtUtc) >= startUtc 
                        && (b.ClosedAtUtc ?? b.CreatedAtUtc) < endUtc);
                if (!hasToday)
                {
                    var latestBill = await _context.Bills.IgnoreQueryFilters()
                        .Where(b => b.RestaurantId == restaurant!.Id 
                            && b.Status == BillStatus.Paid 
                            && b.Status != BillStatus.Cancelled
                            && (b.Order == null || b.Order.Status != OrderStatus.Cancelled))
                        .OrderByDescending(b => b.ClosedAtUtc ?? b.CreatedAtUtc)
                        .FirstOrDefaultAsync();
                    if (latestBill != null)
                    {
                        targetDate = _timeHelper.ToLocal(latestBill.ClosedAtUtc ?? latestBill.CreatedAtUtc).Date;
                        (startUtc, endUtc) = _timeHelper.LocalDayRangeUtc(targetDate);
                    }
                }
            }

            // Bills for selected IST date
            var bills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant!.Id 
                    && b.Status == BillStatus.Paid 
                    && b.Status != BillStatus.Cancelled
                    && (b.Order == null || b.Order.Status != OrderStatus.Cancelled)
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) >= startUtc 
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) < endUtc)
                .Include(b => b.Payments)
                .Include(b => b.Order)
                    .ThenInclude(o => o!.Table)
                .Include(b => b.Order)
                    .ThenInclude(o => o!.OrderItems)
                .OrderByDescending(b => b.ClosedAtUtc ?? b.CreatedAtUtc)
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

            // Hourly trend calculation (all 24 hours 0 to 23)
            var hourlyLabels = new List<string>();
            var hourlyData = new List<decimal>();
            for (int h = 0; h < 24; h++)
            {
                var hourDt = new DateTime(2000, 1, 1, h, 0, 0);
                hourlyLabels.Add(hourDt.ToString("h tt"));
                var hourSales = bills
                    .Where(b => _timeHelper.ToLocal(b.ClosedAtUtc ?? b.CreatedAtUtc).Hour == h)
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

            int targetYear;
            int targetMonth;

            if (year.HasValue || month.HasValue)
            {
                (targetYear, targetMonth) = _timeHelper.SafeMonthYear(year, month);
            }
            else
            {
                var today = _timeHelper.Today();
                targetYear = today.Year;
                targetMonth = today.Month;

                var (curStartUtc, curEndUtc) = _timeHelper.LocalMonthRangeUtc(targetYear, targetMonth);
                var hasThisMonth = await _context.Bills.IgnoreQueryFilters()
                    .AnyAsync(b => b.RestaurantId == restaurant!.Id 
                        && b.Status == BillStatus.Paid 
                        && b.Status != BillStatus.Cancelled
                        && (b.Order == null || b.Order.Status != OrderStatus.Cancelled)
                        && (b.ClosedAtUtc ?? b.CreatedAtUtc) >= curStartUtc 
                        && (b.ClosedAtUtc ?? b.CreatedAtUtc) < curEndUtc);
                if (!hasThisMonth)
                {
                    var latestBill = await _context.Bills.IgnoreQueryFilters()
                        .Where(b => b.RestaurantId == restaurant!.Id 
                            && b.Status == BillStatus.Paid 
                            && b.Status != BillStatus.Cancelled
                            && (b.Order == null || b.Order.Status != OrderStatus.Cancelled))
                        .OrderByDescending(b => b.ClosedAtUtc ?? b.CreatedAtUtc)
                        .FirstOrDefaultAsync();
                    if (latestBill != null)
                    {
                        var localDt = _timeHelper.ToLocal(latestBill.ClosedAtUtc ?? latestBill.CreatedAtUtc);
                        (targetYear, targetMonth) = _timeHelper.SafeMonthYear(localDt.Year, localDt.Month);
                    }
                }
            }

            var (startOfMonthUtc, endOfMonthUtc) = _timeHelper.LocalMonthRangeUtc(targetYear, targetMonth);

            var bills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant!.Id 
                    && b.Status == BillStatus.Paid 
                    && b.Status != BillStatus.Cancelled
                    && (b.Order == null || b.Order.Status != OrderStatus.Cancelled)
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) >= startOfMonthUtc 
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) < endOfMonthUtc)
                .Include(b => b.Payments)
                .Include(b => b.Order)
                    .ThenInclude(o => o!.OrderItems)
                        .ThenInclude(oi => oi.MenuItem)
                            .ThenInclude(mi => mi!.Category)
                .OrderBy(b => b.ClosedAtUtc ?? b.CreatedAtUtc)
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
                var dayBills = bills.Where(b => _timeHelper.ToLocal(b.ClosedAtUtc ?? b.CreatedAtUtc).Date == curDate.Date).ToList();
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
                Restaurant = restaurant!,
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
        // 3. EXPORT EXCEL (.xlsx) & CSV
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> ExportDailyExcel(DateTime? date = null)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var targetDate = date?.Date ?? _timeHelper.Today();
            var (startUtc, endUtc) = _timeHelper.LocalDayRangeUtc(targetDate);

            var bills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant!.Id 
                    && b.Status == BillStatus.Paid 
                    && b.Status != BillStatus.Cancelled
                    && (b.Order == null || b.Order.Status != OrderStatus.Cancelled)
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) >= startUtc 
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) < endUtc)
                .Include(b => b.Payments)
                .Include(b => b.Order)
                    .ThenInclude(o => o!.Table)
                .Include(b => b.Order)
                    .ThenInclude(o => o!.OrderItems)
                .OrderByDescending(b => b.ClosedAtUtc ?? b.CreatedAtUtc)
                .ToListAsync();

            using var workbook = new XLWorkbook();

            // ------------------------------------------
            // Sheet 1: Settled Bills & Orders
            // ------------------------------------------
            var wsOrders = workbook.Worksheets.Add("Settled Orders");
            wsOrders.Cell(1, 1).Value = $"{restaurant!.Name} - Daily Sales Report";
            wsOrders.Cell(1, 1).Style.Font.Bold = true;
            wsOrders.Cell(1, 1).Style.Font.FontSize = 14;
            wsOrders.Range(1, 1, 1, 8).Merge();

            wsOrders.Cell(2, 1).Value = $"Date: {targetDate:dd MMMM yyyy} | Exported: {_timeHelper.Format(_timeHelper.Now(), "dd MMM yyyy, hh:mm tt")}";
            wsOrders.Cell(2, 1).Style.Font.Italic = true;
            wsOrders.Cell(2, 1).Style.Font.FontColor = XLColor.DarkGray;
            wsOrders.Range(2, 1, 2, 8).Merge();

            var orderHeaders = new[]
            {
                "Bill No", "Order No", "Table", "Payment Method", "Settled Time",
                "Subtotal (₹)", "Tax / GST (₹)", "Grand Total (₹)"
            };

            for (int col = 0; col < orderHeaders.Length; col++)
            {
                var cell = wsOrders.Cell(4, col + 1);
                cell.Value = orderHeaders[col];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromArgb(24, 18, 14);
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = col switch
                {
                    0 or 1 or 2 or 3 => XLAlignmentHorizontalValues.Left,
                    4 => XLAlignmentHorizontalValues.Center,
                    _ => XLAlignmentHorizontalValues.Right
                };
            }

            int row = 5;
            foreach (var b in bills)
            {
                var settledTimeLocal = _timeHelper.ToLocal(b.ClosedAtUtc ?? b.CreatedAtUtc);
                var payMethod = b.Payments.FirstOrDefault()?.Method.ToString() ?? "Settled";

                wsOrders.Cell(row, 1).Value = b.BillNumber;
                wsOrders.Cell(row, 2).Value = b.Order?.OrderNumber ?? "N/A";
                wsOrders.Cell(row, 3).Value = b.Order?.Table?.TableNumber ?? "Table";
                wsOrders.Cell(row, 4).Value = payMethod;
                wsOrders.Cell(row, 5).Value = settledTimeLocal.ToString("hh:mm tt");
                wsOrders.Cell(row, 6).Value = (double)b.SubTotal;
                wsOrders.Cell(row, 6).Style.NumberFormat.Format = "₹#,##0.00";
                wsOrders.Cell(row, 7).Value = (double)b.TaxAmount;
                wsOrders.Cell(row, 7).Style.NumberFormat.Format = "₹#,##0.00";
                wsOrders.Cell(row, 8).Value = (double)b.GrandTotal;
                wsOrders.Cell(row, 8).Style.NumberFormat.Format = "₹#,##0.00";
                wsOrders.Cell(row, 8).Style.Font.Bold = true;
                row++;
            }

            if (bills.Count > 0)
            {
                wsOrders.Cell(row, 1).Value = "TOTAL";
                wsOrders.Cell(row, 1).Style.Font.Bold = true;
                wsOrders.Cell(row, 2).Value = $"{bills.Count} Bills";
                wsOrders.Cell(row, 2).Style.Font.Bold = true;
                wsOrders.Cell(row, 6).Value = (double)bills.Sum(b => b.SubTotal);
                wsOrders.Cell(row, 6).Style.NumberFormat.Format = "₹#,##0.00";
                wsOrders.Cell(row, 6).Style.Font.Bold = true;
                wsOrders.Cell(row, 7).Value = (double)bills.Sum(b => b.TaxAmount);
                wsOrders.Cell(row, 7).Style.NumberFormat.Format = "₹#,##0.00";
                wsOrders.Cell(row, 7).Style.Font.Bold = true;
                wsOrders.Cell(row, 8).Value = (double)bills.Sum(b => b.GrandTotal);
                wsOrders.Cell(row, 8).Style.NumberFormat.Format = "₹#,##0.00";
                wsOrders.Cell(row, 8).Style.Font.Bold = true;

                wsOrders.Range(row, 1, row, 8).Style.Fill.BackgroundColor = XLColor.FromArgb(246, 239, 230);
                wsOrders.Range(row, 1, row, 8).Style.Border.TopBorder = XLBorderStyleValues.Thin;
                wsOrders.Range(row, 1, row, 8).Style.Border.BottomBorder = XLBorderStyleValues.Double;
            }

            wsOrders.ShowGridLines = true;
            wsOrders.Columns().AdjustToContents();

            // ------------------------------------------
            // Sheet 2: Items Sold Breakdown
            // ------------------------------------------
            var wsItems = workbook.Worksheets.Add("Item Breakdown");
            wsItems.Cell(1, 1).Value = $"{restaurant!.Name} - Daily Item Sales Breakdown";
            wsItems.Cell(1, 1).Style.Font.Bold = true;
            wsItems.Cell(1, 1).Style.Font.FontSize = 14;
            wsItems.Range(1, 1, 1, 5).Merge();

            wsItems.Cell(2, 1).Value = $"Date: {targetDate:dd MMMM yyyy}";
            wsItems.Cell(2, 1).Style.Font.Italic = true;
            wsItems.Cell(2, 1).Style.Font.FontColor = XLColor.DarkGray;
            wsItems.Range(2, 1, 2, 5).Merge();

            var itemHeaders = new[] { "Item Name", "Quantity Sold", "Avg. Unit Price (₹)", "Total Sales (₹)", "% of Sales" };
            for (int col = 0; col < itemHeaders.Length; col++)
            {
                var cell = wsItems.Cell(4, col + 1);
                cell.Value = itemHeaders[col];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromArgb(24, 18, 14);
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = col == 0 ? XLAlignmentHorizontalValues.Left : XLAlignmentHorizontalValues.Right;
            }

            var allOrderItems = bills
                .Where(b => b.Order != null)
                .SelectMany(b => b.Order!.OrderItems)
                .GroupBy(i => i.ItemNameSnapshot)
                .Select(g => new
                {
                    Name = g.Key,
                    Quantity = g.Sum(x => x.Quantity),
                    Total = g.Sum(x => x.TotalPrice),
                    UnitPrice = g.Any() ? g.Average(x => x.UnitPriceSnapshot) : 0m
                })
                .OrderByDescending(x => x.Quantity)
                .ToList();

            var grandItemTotal = allOrderItems.Sum(x => x.Total);
            int itemRow = 5;
            foreach (var item in allOrderItems)
            {
                wsItems.Cell(itemRow, 1).Value = item.Name;
                wsItems.Cell(itemRow, 2).Value = item.Quantity;
                wsItems.Cell(itemRow, 2).Style.NumberFormat.Format = "#,##0";
                wsItems.Cell(itemRow, 3).Value = (double)item.UnitPrice;
                wsItems.Cell(itemRow, 3).Style.NumberFormat.Format = "₹#,##0.00";
                wsItems.Cell(itemRow, 4).Value = (double)item.Total;
                wsItems.Cell(itemRow, 4).Style.NumberFormat.Format = "₹#,##0.00";
                wsItems.Cell(itemRow, 5).Value = grandItemTotal > 0 ? (double)(item.Total / grandItemTotal) : 0.0;
                wsItems.Cell(itemRow, 5).Style.NumberFormat.Format = "0.0%";
                itemRow++;
            }

            if (allOrderItems.Count > 0)
            {
                wsItems.Cell(itemRow, 1).Value = "TOTAL";
                wsItems.Cell(itemRow, 1).Style.Font.Bold = true;
                wsItems.Cell(itemRow, 2).Value = allOrderItems.Sum(x => x.Quantity);
                wsItems.Cell(itemRow, 2).Style.NumberFormat.Format = "#,##0";
                wsItems.Cell(itemRow, 2).Style.Font.Bold = true;
                wsItems.Cell(itemRow, 4).Value = (double)grandItemTotal;
                wsItems.Cell(itemRow, 4).Style.NumberFormat.Format = "₹#,##0.00";
                wsItems.Cell(itemRow, 4).Style.Font.Bold = true;
                wsItems.Cell(itemRow, 5).Value = 1.0;
                wsItems.Cell(itemRow, 5).Style.NumberFormat.Format = "0.0%";
                wsItems.Cell(itemRow, 5).Style.Font.Bold = true;

                wsItems.Range(itemRow, 1, itemRow, 5).Style.Fill.BackgroundColor = XLColor.FromArgb(246, 239, 230);
                wsItems.Range(itemRow, 1, itemRow, 5).Style.Border.TopBorder = XLBorderStyleValues.Thin;
                wsItems.Range(itemRow, 1, itemRow, 5).Style.Border.BottomBorder = XLBorderStyleValues.Double;
            }

            wsItems.ShowGridLines = true;
            wsItems.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var content = stream.ToArray();

            var safeSlug = string.IsNullOrWhiteSpace(restaurant.Slug) ? "restaurant" : restaurant.Slug;
            var filename = $"DailySales_{safeSlug}_{targetDate:yyyy-MM-dd}.xlsx";
            return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
        }

        [HttpGet]
        public async Task<IActionResult> ExportMonthlyExcel(int? year, int? month)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var (targetYear, targetMonth) = _timeHelper.SafeMonthYear(year, month);
            var (startOfMonthUtc, endOfMonthUtc) = _timeHelper.LocalMonthRangeUtc(targetYear, targetMonth);

            var bills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant!.Id 
                    && b.Status == BillStatus.Paid 
                    && b.Status != BillStatus.Cancelled
                    && (b.Order == null || b.Order.Status != OrderStatus.Cancelled)
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) >= startOfMonthUtc 
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) < endOfMonthUtc)
                .Include(b => b.Payments)
                .ToListAsync();

            var daysInMonth = DateTime.DaysInMonth(targetYear, targetMonth);
            var monthDisplay = new DateTime(targetYear, targetMonth, 1).ToString("MMMM yyyy");

            using var workbook = new XLWorkbook();

            // ------------------------------------------
            // Sheet 1: Daily Sales & Settlements
            // ------------------------------------------
            var ws = workbook.Worksheets.Add("Monthly Sales");

            ws.Cell(1, 1).Value = $"{restaurant!.Name} - Monthly Sales & Settlement Report";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Range(1, 1, 1, 9).Merge();

            ws.Cell(2, 1).Value = $"Period: {monthDisplay} | Exported On: {_timeHelper.Format(_timeHelper.Now(), "dd MMM yyyy, hh:mm tt")}";
            ws.Cell(2, 1).Style.Font.Italic = true;
            ws.Cell(2, 1).Style.Font.FontColor = XLColor.DarkGray;
            ws.Range(2, 1, 2, 9).Merge();

            var headers = new[]
            {
                "Date", "Day of Week", "Orders Count", "GST / Tax (₹)",
                "Net Settled Sales (₹)", "UPI Payment (₹)", "Cash Counter (₹)",
                "Card Payment (₹)", "Other Payment (₹)"
            };

            for (int col = 0; col < headers.Length; col++)
            {
                var cell = ws.Cell(4, col + 1);
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromArgb(24, 18, 14);
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = col switch
                {
                    0 or 1 => XLAlignmentHorizontalValues.Left,
                    2 => XLAlignmentHorizontalValues.Center,
                    _ => XLAlignmentHorizontalValues.Right
                };
            }

            int currentRow = 5;
            int totalOrders = 0;
            decimal totalTax = 0;
            decimal totalMonthlySales = 0;
            decimal totalUpi = 0;
            decimal totalCash = 0;
            decimal totalCard = 0;
            decimal totalOther = 0;

            for (int day = 1; day <= daysInMonth; day++)
            {
                var localDate = new DateTime(targetYear, targetMonth, day);
                var (dayStartUtc, dayEndUtc) = _timeHelper.LocalDayRangeUtc(localDate);

                var dayBills = bills.Where(b => (b.ClosedAtUtc ?? b.CreatedAtUtc) >= dayStartUtc && (b.ClosedAtUtc ?? b.CreatedAtUtc) < dayEndUtc).ToList();
                var dayOrderCount = dayBills.Count;
                var dayTax = dayBills.Sum(b => b.TaxAmount);
                var daySales = dayBills.Sum(b => b.GrandTotal);

                var dayUpi = dayBills.SelectMany(b => b.Payments).Where(p => p.Method == PaymentMethod.UPI).Sum(p => p.Amount);
                var dayCash = dayBills.SelectMany(b => b.Payments).Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
                var dayCard = dayBills.SelectMany(b => b.Payments).Where(p => p.Method == PaymentMethod.Card).Sum(p => p.Amount);
                var dayOther = dayBills.SelectMany(b => b.Payments).Where(p => p.Method == PaymentMethod.Other).Sum(p => p.Amount);

                totalOrders += dayOrderCount;
                totalTax += dayTax;
                totalMonthlySales += daySales;
                totalUpi += dayUpi;
                totalCash += dayCash;
                totalCard += dayCard;
                totalOther += dayOther;

                ws.Cell(currentRow, 1).Value = localDate.ToString("yyyy-MM-dd");
                ws.Cell(currentRow, 2).Value = localDate.ToString("dddd");
                ws.Cell(currentRow, 3).Value = dayOrderCount;
                ws.Cell(currentRow, 3).Style.NumberFormat.Format = "#,##0";
                ws.Cell(currentRow, 4).Value = (double)dayTax;
                ws.Cell(currentRow, 4).Style.NumberFormat.Format = "₹#,##0.00";
                ws.Cell(currentRow, 5).Value = (double)daySales;
                ws.Cell(currentRow, 5).Style.NumberFormat.Format = "₹#,##0.00";
                ws.Cell(currentRow, 5).Style.Font.Bold = true;
                ws.Cell(currentRow, 6).Value = (double)dayUpi;
                ws.Cell(currentRow, 6).Style.NumberFormat.Format = "₹#,##0.00";
                ws.Cell(currentRow, 7).Value = (double)dayCash;
                ws.Cell(currentRow, 7).Style.NumberFormat.Format = "₹#,##0.00";
                ws.Cell(currentRow, 8).Value = (double)dayCard;
                ws.Cell(currentRow, 8).Style.NumberFormat.Format = "₹#,##0.00";
                ws.Cell(currentRow, 9).Value = (double)dayOther;
                ws.Cell(currentRow, 9).Style.NumberFormat.Format = "₹#,##0.00";

                if (localDate.DayOfWeek == DayOfWeek.Saturday || localDate.DayOfWeek == DayOfWeek.Sunday)
                {
                    ws.Range(currentRow, 1, currentRow, 9).Style.Fill.BackgroundColor = XLColor.FromArgb(250, 250, 247);
                }

                currentRow++;
            }

            ws.Cell(currentRow, 1).Value = "TOTAL";
            ws.Cell(currentRow, 1).Style.Font.Bold = true;
            ws.Cell(currentRow, 2).Value = $"{daysInMonth} Days";
            ws.Cell(currentRow, 2).Style.Font.Bold = true;

            ws.Cell(currentRow, 3).Value = totalOrders;
            ws.Cell(currentRow, 3).Style.NumberFormat.Format = "#,##0";
            ws.Cell(currentRow, 3).Style.Font.Bold = true;

            ws.Cell(currentRow, 4).Value = (double)totalTax;
            ws.Cell(currentRow, 4).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(currentRow, 4).Style.Font.Bold = true;

            ws.Cell(currentRow, 5).Value = (double)totalMonthlySales;
            ws.Cell(currentRow, 5).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(currentRow, 5).Style.Font.Bold = true;

            ws.Cell(currentRow, 6).Value = (double)totalUpi;
            ws.Cell(currentRow, 6).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(currentRow, 6).Style.Font.Bold = true;

            ws.Cell(currentRow, 7).Value = (double)totalCash;
            ws.Cell(currentRow, 7).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(currentRow, 7).Style.Font.Bold = true;

            ws.Cell(currentRow, 8).Value = (double)totalCard;
            ws.Cell(currentRow, 8).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(currentRow, 8).Style.Font.Bold = true;

            ws.Cell(currentRow, 9).Value = (double)totalOther;
            ws.Cell(currentRow, 9).Style.NumberFormat.Format = "₹#,##0.00";
            ws.Cell(currentRow, 9).Style.Font.Bold = true;

            ws.Range(currentRow, 1, currentRow, 9).Style.Fill.BackgroundColor = XLColor.FromArgb(246, 239, 230);
            ws.Range(currentRow, 1, currentRow, 9).Style.Border.TopBorder = XLBorderStyleValues.Thin;
            ws.Range(currentRow, 1, currentRow, 9).Style.Border.BottomBorder = XLBorderStyleValues.Double;

            ws.ShowGridLines = true;
            ws.Columns().AdjustToContents();

            // ------------------------------------------
            // Sheet 2: Cashier & Settlement Breakdown
            // ------------------------------------------
            var wsCashiers = workbook.Worksheets.Add("Cashier Breakdown");
            wsCashiers.Cell(1, 1).Value = $"{restaurant!.Name} - Cashier Settlement Breakdown";
            wsCashiers.Cell(1, 1).Style.Font.Bold = true;
            wsCashiers.Cell(1, 1).Style.Font.FontSize = 14;
            wsCashiers.Range(1, 1, 1, 7).Merge();

            wsCashiers.Cell(2, 1).Value = $"Period: {monthDisplay}";
            wsCashiers.Cell(2, 1).Style.Font.Italic = true;
            wsCashiers.Cell(2, 1).Style.Font.FontColor = XLColor.DarkGray;
            wsCashiers.Range(2, 1, 2, 7).Merge();

            var cashierHeaders = new[]
            {
                "Cashier / Source", "Bills Settled", "Cash Counter (₹)",
                "UPI Collected (₹)", "Card Collected (₹)", "Total Revenue Handled (₹)", "Revenue Share"
            };

            for (int col = 0; col < cashierHeaders.Length; col++)
            {
                var cell = wsCashiers.Cell(4, col + 1);
                cell.Value = cashierHeaders[col];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromArgb(24, 18, 14);
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = col == 0 ? XLAlignmentHorizontalValues.Left : XLAlignmentHorizontalValues.Right;
            }

            var totalSales = bills.Sum(b => b.GrandTotal);
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
                    PercentageOfTotal = totalSales > 0 ? (double)(g.Select(x => x.Bill).Distinct().Sum(x => x.GrandTotal) / totalSales) * 100 : 0.0
                })
                .OrderByDescending(x => x.TotalRevenue)
                .ToList();

            int cRow = 5;
            foreach (var c in excelCashiers)
            {
                wsCashiers.Cell(cRow, 1).Value = c.CashierName;
                wsCashiers.Cell(cRow, 2).Value = c.BillsCount;
                wsCashiers.Cell(cRow, 2).Style.NumberFormat.Format = "#,##0";
                wsCashiers.Cell(cRow, 3).Value = (double)c.CashCollected;
                wsCashiers.Cell(cRow, 3).Style.NumberFormat.Format = "₹#,##0.00";
                wsCashiers.Cell(cRow, 4).Value = (double)c.UpiCollected;
                wsCashiers.Cell(cRow, 4).Style.NumberFormat.Format = "₹#,##0.00";
                wsCashiers.Cell(cRow, 5).Value = (double)c.CardCollected;
                wsCashiers.Cell(cRow, 5).Style.NumberFormat.Format = "₹#,##0.00";
                wsCashiers.Cell(cRow, 6).Value = (double)c.TotalRevenue;
                wsCashiers.Cell(cRow, 6).Style.NumberFormat.Format = "₹#,##0.00";
                wsCashiers.Cell(cRow, 6).Style.Font.Bold = true;
                wsCashiers.Cell(cRow, 7).Value = c.PercentageOfTotal / 100.0;
                wsCashiers.Cell(cRow, 7).Style.NumberFormat.Format = "0.0%";
                cRow++;
            }

            if (excelCashiers.Count > 0)
            {
                wsCashiers.Cell(cRow, 1).Value = "TOTAL";
                wsCashiers.Cell(cRow, 1).Style.Font.Bold = true;
                wsCashiers.Cell(cRow, 2).Value = excelCashiers.Sum(x => x.BillsCount);
                wsCashiers.Cell(cRow, 2).Style.NumberFormat.Format = "#,##0";
                wsCashiers.Cell(cRow, 2).Style.Font.Bold = true;
                wsCashiers.Cell(cRow, 3).Value = (double)excelCashiers.Sum(x => x.CashCollected);
                wsCashiers.Cell(cRow, 3).Style.NumberFormat.Format = "₹#,##0.00";
                wsCashiers.Cell(cRow, 3).Style.Font.Bold = true;
                wsCashiers.Cell(cRow, 4).Value = (double)excelCashiers.Sum(x => x.UpiCollected);
                wsCashiers.Cell(cRow, 4).Style.NumberFormat.Format = "₹#,##0.00";
                wsCashiers.Cell(cRow, 4).Style.Font.Bold = true;
                wsCashiers.Cell(cRow, 5).Value = (double)excelCashiers.Sum(x => x.CardCollected);
                wsCashiers.Cell(cRow, 5).Style.NumberFormat.Format = "₹#,##0.00";
                wsCashiers.Cell(cRow, 5).Style.Font.Bold = true;
                wsCashiers.Cell(cRow, 6).Value = (double)excelCashiers.Sum(x => x.TotalRevenue);
                wsCashiers.Cell(cRow, 6).Style.NumberFormat.Format = "₹#,##0.00";
                wsCashiers.Cell(cRow, 6).Style.Font.Bold = true;
                wsCashiers.Cell(cRow, 7).Value = 1.0;
                wsCashiers.Cell(cRow, 7).Style.NumberFormat.Format = "0.0%";
                wsCashiers.Cell(cRow, 7).Style.Font.Bold = true;

                wsCashiers.Range(cRow, 1, cRow, 7).Style.Fill.BackgroundColor = XLColor.FromArgb(246, 239, 230);
                wsCashiers.Range(cRow, 1, cRow, 7).Style.Border.TopBorder = XLBorderStyleValues.Thin;
                wsCashiers.Range(cRow, 1, cRow, 7).Style.Border.BottomBorder = XLBorderStyleValues.Double;
            }

            wsCashiers.ShowGridLines = true;
            wsCashiers.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var content = stream.ToArray();

            var safeSlug = string.IsNullOrWhiteSpace(restaurant.Slug) ? "restaurant" : restaurant.Slug;
            var filename = $"MonthlySales_{safeSlug}_{targetYear}_{targetMonth:D2}.xlsx";
            return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
        }

        [HttpGet]
        public async Task<IActionResult> ExportMonthlyCsv(int? year, int? month)
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var (targetYear, targetMonth) = _timeHelper.SafeMonthYear(year, month);
            var (startOfMonthUtc, endOfMonthUtc) = _timeHelper.LocalMonthRangeUtc(targetYear, targetMonth);

            var bills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant!.Id 
                    && b.Status == BillStatus.Paid 
                    && b.Status != BillStatus.Cancelled
                    && (b.Order == null || b.Order.Status != OrderStatus.Cancelled)
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) >= startOfMonthUtc 
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) < endOfMonthUtc)
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
                var dayBills = bills.Where(b => _timeHelper.ToLocal(b.ClosedAtUtc ?? b.CreatedAtUtc).Date == curDate.Date).ToList();
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

                sb.AppendLine($"{EscapeCsvCell(curDate.ToString("yyyy-MM-dd"))},{EscapeCsvCell(curDate.ToString("dddd"))},{dayBills.Count},{dayTax:F2},{daySales:F2},{dayUpi:F2},{dayCash:F2},{dayCard:F2}");
            }

            sb.AppendLine($"{EscapeCsvCell("TOTAL")},{EscapeCsvCell("All Days")},{totalOrders},{totalTax:F2},{totalSales:F2},{totalUpi:F2},{totalCash:F2},{totalCard:F2}");

            // Cashier settlement section in CSV
            sb.AppendLine();
            sb.AppendLine("Cashier / Source,Bills Settled,Cash Counter (INR),UPI Collected (INR),Card Collected (INR),Total Revenue Handled (INR),Revenue Share (%)");

            var csvCashiers = bills
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

            foreach (var c in csvCashiers)
            {
                sb.AppendLine($"{EscapeCsvCell(c.CashierName)},{c.BillsCount},{c.CashCollected:F2},{c.UpiCollected:F2},{c.CardCollected:F2},{c.TotalRevenue:F2},{c.PercentageOfTotal:0.0}%");
            }

            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            var cleanName = System.Text.RegularExpressions.Regex.Replace(restaurant!.Name, @"[^a-zA-Z0-9]", "_");
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

            var targetDate = date?.Date ?? _timeHelper.Today();
            var (startUtc, endUtc) = _timeHelper.LocalDayRangeUtc(targetDate);

            var bills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant!.Id 
                    && b.Status == BillStatus.Paid 
                    && b.Status != BillStatus.Cancelled
                    && (b.Order == null || b.Order.Status != OrderStatus.Cancelled)
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) >= startUtc 
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) < endUtc)
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

            var (targetYear, targetMonth) = _timeHelper.SafeMonthYear(year, month);
            var (startOfMonthUtc, endOfMonthUtc) = _timeHelper.LocalMonthRangeUtc(targetYear, targetMonth);

            var bills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurant!.Id 
                    && b.Status == BillStatus.Paid 
                    && b.Status != BillStatus.Cancelled
                    && (b.Order == null || b.Order.Status != OrderStatus.Cancelled)
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) >= startOfMonthUtc 
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) < endOfMonthUtc)
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

            var activeDays = bills.Select(b => _timeHelper.ToLocal(b.ClosedAtUtc ?? b.CreatedAtUtc).Date).Distinct().Count();
            var dailyAverage = activeDays > 0 ? totalSales / activeDays : 0;

            var topItems = bills.SelectMany(b => b.Order?.OrderItems ?? Enumerable.Empty<OrderItem>())
                .GroupBy(i => i.ItemNameSnapshot)
                .Select(g => (ItemName: g.Key, Quantity: g.Sum(x => x.Quantity), Revenue: g.Sum(x => x.TotalPrice)))
                .OrderByDescending(x => x.Quantity)
                .Take(5)
                .ToList();

            var targetPhone = !string.IsNullOrWhiteSpace(phone) ? phone.Trim() : restaurant!.Phone;

            var msg = WhatsAppHelper.BuildMonthlySalesSummaryMessage(
                restaurant!.Name,
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
                restaurantName = restaurant!.Name,
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
