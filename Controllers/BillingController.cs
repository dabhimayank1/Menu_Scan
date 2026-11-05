using MenuScan.Data;
using MenuScan.Hubs;
using MenuScan.Models.Entities;
using MenuScan.Models.ViewModels;
using MenuScan.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MenuScan.Controllers
{
    [Authorize(Roles = $"{AppRoles.Cashier},{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
    public class BillingController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ITenantService _tenantService;
        private readonly IHubContext<OrderHub> _hubContext;
        private readonly ILogger<BillingController> _logger;
        private readonly IConfiguration _configuration;
        private readonly ITimeHelper _timeHelper;

        public BillingController(
            ApplicationDbContext context,
            ITenantService tenantService,
            IHubContext<OrderHub> hubContext,
            ILogger<BillingController> logger,
            IConfiguration configuration,
            ITimeHelper timeHelper)
        {
            _context = context;
            _tenantService = tenantService;
            _hubContext = hubContext;
            _logger = logger;
            _configuration = configuration;
            _timeHelper = timeHelper;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? tab = "active", DateTime? date = null, int? year = null, int? month = null, string? table = null)
        {
            var restaurantId = _tenantService.CurrentRestaurantId;
            if (!restaurantId.HasValue)
            {
                if (User.IsInRole(AppRoles.SuperAdmin))
                {
                    return RedirectToAction("Index", "SuperAdmin");
                }
                return Forbid();
            }

            var restaurant = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == restaurantId.Value);

            // 1. Fetch active orders requiring settlement (unpaid orders)
            var activeOrdersQuery = _context.Orders
                .IgnoreQueryFilters()
                .Where(o => o.RestaurantId == restaurantId.Value 
                         && o.Status != OrderStatus.Completed 
                         && o.Status != OrderStatus.Cancelled
                         && (o.Bill == null || o.Bill.Status != BillStatus.Paid));

            if (!string.IsNullOrWhiteSpace(table))
            {
                var trimmedTable = table.Trim();
                activeOrdersQuery = activeOrdersQuery.Where(o => o.Table != null && o.Table.TableNumber == trimmedTable);
            }

            var activeOrders = await activeOrdersQuery
                .OrderByDescending(o => o.CreatedAtUtc)
                .Include(o => o.Table)
                .Include(o => o.OrderItems)
                .Include(o => o.Bill)
                .ToListAsync();

            // 2. Fetch unhandled service requests
            var pendingRequests = await _context.ServiceRequests
                .IgnoreQueryFilters()
                .Where(s => s.RestaurantId == restaurantId.Value && !s.IsHandled)
                .Include(s => s.Table)
                .OrderByDescending(s => s.CreatedAtUtc)
                .ToListAsync();

            // 3. DAILY Cashier & Settlement Data
            var targetDate = date?.Date ?? _timeHelper.Today();
            var (startUtc, endUtc) = _timeHelper.LocalDayRangeUtc(targetDate);

            if (!date.HasValue)
            {
                var hasToday = await _context.Bills.IgnoreQueryFilters()
                    .AnyAsync(b => b.RestaurantId == restaurantId.Value 
                        && b.Status == BillStatus.Paid 
                        && b.Status != BillStatus.Cancelled
                        && (b.Order == null || b.Order.Status != OrderStatus.Cancelled)
                        && (b.ClosedAtUtc ?? b.CreatedAtUtc) >= startUtc 
                        && (b.ClosedAtUtc ?? b.CreatedAtUtc) < endUtc);
                if (!hasToday)
                {
                    var latestBill = await _context.Bills.IgnoreQueryFilters()
                        .Where(b => b.RestaurantId == restaurantId.Value 
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

            var dailySettledBills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurantId.Value 
                    && b.Status == BillStatus.Paid 
                    && b.Status != BillStatus.Cancelled
                    && (b.Order == null || b.Order.Status != OrderStatus.Cancelled)
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) >= startUtc 
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) < endUtc)
                .Include(b => b.Payments)
                .Include(b => b.Order)
                    .ThenInclude(o => o!.Table)
                .OrderByDescending(b => b.ClosedAtUtc ?? b.CreatedAtUtc)
                .ToListAsync();

            var dailyTotalSales = dailySettledBills.Sum(b => b.GrandTotal);
            var dailyTotalTax = dailySettledBills.Sum(b => b.TaxAmount);
            var dailyTotalBills = dailySettledBills.Count;
            var dailyPayments = dailySettledBills.SelectMany(b => b.Payments).ToList();
            var dailyTotalCash = dailyPayments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
            var dailyTotalUpi = dailyPayments.Where(p => p.Method == PaymentMethod.UPI).Sum(p => p.Amount);
            var dailyTotalCard = dailyPayments.Where(p => p.Method == PaymentMethod.Card).Sum(p => p.Amount);

            var dailyCashiers = dailySettledBills
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
                    PercentageOfTotal = dailyTotalSales > 0 ? (double)(g.Select(x => x.Bill).Distinct().Sum(x => x.GrandTotal) / dailyTotalSales) * 100 : 0
                })
                .OrderByDescending(x => x.TotalRevenue)
                .ToList();

            // 4. MONTHLY Cashier & Settlement Data
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
                    .AnyAsync(b => b.RestaurantId == restaurantId.Value 
                        && b.Status == BillStatus.Paid 
                        && b.Status != BillStatus.Cancelled
                        && (b.Order == null || b.Order.Status != OrderStatus.Cancelled)
                        && (b.ClosedAtUtc ?? b.CreatedAtUtc) >= curStartUtc 
                        && (b.ClosedAtUtc ?? b.CreatedAtUtc) < curEndUtc);
                if (!hasThisMonth)
                {
                    var latestBill = await _context.Bills.IgnoreQueryFilters()
                        .Where(b => b.RestaurantId == restaurantId.Value 
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

            var monthlySettledBills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurantId.Value 
                    && b.Status == BillStatus.Paid 
                    && b.Status != BillStatus.Cancelled
                    && (b.Order == null || b.Order.Status != OrderStatus.Cancelled)
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) >= startOfMonthUtc 
                    && (b.ClosedAtUtc ?? b.CreatedAtUtc) < endOfMonthUtc)
                .Include(b => b.Payments)
                .Include(b => b.Order)
                    .ThenInclude(o => o!.Table)
                .OrderByDescending(b => b.ClosedAtUtc ?? b.CreatedAtUtc)
                .ToListAsync();

            var monthTotalSales = monthlySettledBills.Sum(b => b.GrandTotal);
            var monthTotalTax = monthlySettledBills.Sum(b => b.TaxAmount);
            var monthTotalBills = monthlySettledBills.Count;
            var monthPayments = monthlySettledBills.SelectMany(b => b.Payments).ToList();
            var monthTotalCash = monthPayments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
            var monthTotalUpi = monthPayments.Where(p => p.Method == PaymentMethod.UPI).Sum(p => p.Amount);
            var monthTotalCard = monthPayments.Where(p => p.Method == PaymentMethod.Card).Sum(p => p.Amount);

            var monthlyCashiers = monthlySettledBills
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
                    PercentageOfTotal = monthTotalSales > 0 ? (double)(g.Select(x => x.Bill).Distinct().Sum(x => x.GrandTotal) / monthTotalSales) * 100 : 0
                })
                .OrderByDescending(x => x.TotalRevenue)
                .ToList();

            ViewBag.RestaurantId = restaurantId.Value;
            ViewBag.Restaurant = restaurant;
            ViewBag.ActiveTab = tab ?? "active";
            ViewBag.SelectedTable = table;
            ViewBag.PendingRequests = pendingRequests;

            ViewBag.SelectedDate = targetDate;
            ViewBag.DailySettledBills = dailySettledBills;
            ViewBag.DailyCashiers = dailyCashiers;
            ViewBag.DailyTotalSales = dailyTotalSales;
            ViewBag.DailyTotalTax = dailyTotalTax;
            ViewBag.DailyTotalCash = dailyTotalCash;
            ViewBag.DailyTotalUpi = dailyTotalUpi;
            ViewBag.DailyTotalCard = dailyTotalCard;
            ViewBag.DailyTotalBills = dailyTotalBills;

            ViewBag.SelectedYear = targetYear;
            ViewBag.SelectedMonth = targetMonth;
            ViewBag.MonthlySettledBills = monthlySettledBills;
            ViewBag.MonthlyCashiers = monthlyCashiers;
            ViewBag.MonthTotalSales = monthTotalSales;
            ViewBag.MonthTotalTax = monthTotalTax;
            ViewBag.MonthTotalCash = monthTotalCash;
            ViewBag.MonthTotalUpi = monthTotalUpi;
            ViewBag.MonthTotalCard = monthTotalCard;
            ViewBag.MonthTotalBills = monthTotalBills;

            return View(activeOrders);
        }

        [HttpPost]
        [Authorize(Roles = $"{AppRoles.Cashier},{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> CancelOrder([FromBody] CancelOrderDto? dto)
        {
            if (dto == null || !ModelState.IsValid || string.IsNullOrWhiteSpace(dto.Reason))
            {
                return BadRequest(new { message = "Cancellation reason is required." });
            }

            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Table)
                .Include(o => o.Bill)
                .FirstOrDefaultAsync(o => o.Id == dto.OrderId);

            if (order == null)
            {
                return NotFound(new { message = "Order not found." });
            }

            var currentTenantId = _tenantService.CurrentRestaurantId;
            if (!User.IsInRole(AppRoles.SuperAdmin) && (!currentTenantId.HasValue || order.RestaurantId != currentTenantId.Value))
            {
                return Forbid();
            }

            // Only if bill is not Paid and order is not Completed
            if (order.Status == OrderStatus.Completed || (order.Bill != null && order.Bill.Status == BillStatus.Paid))
            {
                return BadRequest(new { message = "Cannot cancel an order that is already completed or paid." });
            }

            if (order.Status == OrderStatus.Cancelled)
            {
                return Ok(new { success = true, message = "Order is already cancelled." });
            }

            var oldStatus = order.Status;
            order.Status = OrderStatus.Cancelled;
            if (order.Bill != null && order.Bill.Status == BillStatus.Unpaid)
            {
                order.Bill.Status = BillStatus.Cancelled;
            }

            _context.OrderStatusHistories.Add(new OrderStatusHistory
            {
                OrderId = order.Id,
                OldStatus = oldStatus,
                NewStatus = OrderStatus.Cancelled,
                ChangedBy = User.Identity?.Name ?? "Cashier",
                Notes = $"Order cancelled: {dto.Reason.Trim()}"
            });

            await _context.SaveChangesAsync();

            // Broadcast to Restaurant Group (Kitchen & Cashier)
            await _hubContext.Clients.Group($"Restaurant_{order.RestaurantId}")
                .SendAsync("ReceiveOrderStatusUpdate", order.OrderNumber, "Cancelled", order.Table?.TableNumber ?? "Unknown", order.Id.ToString());

            // Broadcast to Customer Order Group
            await _hubContext.Clients.Group($"Order_{order.OrderToken}")
                .SendAsync("ReceiveOrderStatusUpdate", order.OrderNumber, "Cancelled", order.Table?.TableNumber ?? "Unknown", order.Id.ToString());

            _logger.LogInformation("Order {OrderNumber} cancelled by {User}. Reason: {Reason}",
                order.OrderNumber, User.Identity?.Name, dto.Reason);

            return Ok(new { success = true, message = "Order cancelled successfully." });
        }

        [HttpPost]
        public async Task<IActionResult> SettleBill([FromBody] SettleBillDto? dto)
        {
            if (dto == null || !ModelState.IsValid)
            {
                return BadRequest(new { message = "Invalid payment data." });
            }

            if (!Enum.IsDefined(typeof(PaymentMethod), dto.Method))
            {
                return BadRequest(new { message = "Invalid payment method." });
            }

            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Restaurant)
                .Include(o => o.Table)
                .Include(o => o.Bill)
                .FirstOrDefaultAsync(o => o.Id == dto.OrderId);

            if (order == null)
            {
                return NotFound(new { message = "Order not found." });
            }

            // Tenant security check
            var currentTenantId = _tenantService.CurrentRestaurantId;
            if (!User.IsInRole(AppRoles.SuperAdmin) && (!currentTenantId.HasValue || order.RestaurantId != currentTenantId.Value))
            {
                return Forbid();
            }

            // Reject cancelled orders
            if (order.Status == OrderStatus.Cancelled)
            {
                return BadRequest(new { message = "Cannot settle a cancelled order." });
            }

            var baseUrl = NetworkHelper.ResolveBaseUrl(Request, _configuration);
            var restName = order.Restaurant?.Name ?? "Brew & Bliss Coffee House";

            // If already settled, do not create duplicate payments or inflate drawer cash
            if (order.Status == OrderStatus.Completed && order.Bill != null && order.Bill.Status == BillStatus.Paid)
            {
                var existingReceiptUrl = $"{baseUrl}/Billing/PrintReceipt?orderId={order.Id}";
                var existingWpUrl = WhatsAppHelper.BuildWhatsAppUrl(
                    order.CustomerPhone,
                    WhatsAppHelper.BuildBillReceiptMessage(restName, order.Bill.BillNumber, order.OrderNumber, order.Table?.TableNumber ?? "Table", order.Bill.GrandTotal, dto.Method.ToString(), existingReceiptUrl),
                    forceWeb: true
                );

                return Ok(new
                {
                    success = true,
                    alreadyPaid = true,
                    billNumber = order.Bill.BillNumber,
                    receiptUrl = existingReceiptUrl,
                    customerPhone = order.CustomerPhone,
                    whatsAppUrl = existingWpUrl
                });
            }

            int attempts = 0;
            const int maxAttempts = 2;

            while (attempts < maxAttempts)
            {
                attempts++;
                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    var bill = order.Bill;
                    if (bill == null)
                    {
                        bill = new Bill
                        {
                            RestaurantId = order.RestaurantId,
                            OrderId = order.Id,
                            BillNumber = GenerateBillNumber(),
                            SubTotal = order.SubTotal,
                            TaxRate = order.TaxRate,
                            TaxAmount = order.TaxAmount,
                            GrandTotal = order.TotalAmount,
                            Status = BillStatus.Paid,
                            ClosedAtUtc = DateTime.UtcNow
                        };
                        _context.Bills.Add(bill);
                    }
                    else
                    {
                        bill.Status = BillStatus.Paid;
                        bill.ClosedAtUtc = DateTime.UtcNow;
                    }

                    // Add payment record
                    var payment = new Payment
                    {
                        BillId = bill.Id,
                        Amount = bill.GrandTotal,
                        Method = dto.Method,
                        Reference = dto.Reference,
                        RecordedBy = User.Identity?.Name ?? "Cashier",
                        PaidAtUtc = DateTime.UtcNow
                    };
                    _context.Payments.Add(payment);

                    // Update order to Completed
                    var oldStatus = order.Status;
                    order.Status = OrderStatus.Completed;

                    _context.OrderStatusHistories.Add(new OrderStatusHistory
                    {
                        OrderId = order.Id,
                        OldStatus = oldStatus,
                        NewStatus = OrderStatus.Completed,
                        ChangedBy = User.Identity?.Name ?? "Cashier",
                        Notes = $"Bill settled via {dto.Method} ({bill.BillNumber})"
                    });

                    // Mark as handled only the service requests of this order's table with RequestType "UPI Payment" or "Bill"
                    var pendingTableRequests = await _context.ServiceRequests
                        .IgnoreQueryFilters()
                        .Where(s => s.RestaurantId == order.RestaurantId
                                 && (s.OrderId == order.Id || (s.TableId == order.TableId && (s.RequestType == "UPI Payment" || s.RequestType == "Bill")))
                                 && (s.RequestType == "UPI Payment" || s.RequestType == "Bill")
                                 && !s.IsHandled)
                        .ToListAsync();

                    foreach (var req in pendingTableRequests)
                    {
                        req.IsHandled = true;
                        req.HandledAtUtc = DateTime.UtcNow;
                        req.HandledBy = User.Identity?.Name ?? "Cashier";
                    }

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // Notify via SignalR
                    await _hubContext.Clients.Group($"Restaurant_{order.RestaurantId}")
                        .SendAsync("ReceiveOrderStatusUpdate", order.OrderNumber, "Completed", order.Table?.TableNumber ?? "", order.Id.ToString());

                    await _hubContext.Clients.Group($"Restaurant_{order.RestaurantId}")
                        .SendAsync("ReceivePaymentSettled", new
                    {
                        orderNumber = order.OrderNumber,
                        tableNumber = order.Table?.TableNumber ?? "Table",
                        amount = bill.GrandTotal,
                        method = dto.Method.ToString(),
                        billNumber = bill.BillNumber
                    });

                    await _hubContext.Clients.Group($"Order_{order.OrderToken}")
                        .SendAsync("ReceiveOrderStatusUpdate", order.OrderNumber, "Completed", order.Table?.TableNumber ?? "", order.Id.ToString());

                    await _hubContext.Clients.Group($"Order_{order.OrderToken}")
                        .SendAsync("ReceivePaymentSettled", new
                    {
                        orderNumber = order.OrderNumber,
                        tableNumber = order.Table?.TableNumber ?? "Table",
                        amount = bill.GrandTotal,
                        method = dto.Method.ToString(),
                        billNumber = bill.BillNumber
                    });

                    _logger.LogInformation("Bill {BillNumber} settled for Order {OrderNumber} amount {Amount} via {Method}",
                        bill.BillNumber, order.OrderNumber, bill.GrandTotal, dto.Method);

                    var fullReceiptUrl = $"{baseUrl}/Billing/PrintReceipt?orderId={order.Id}";
                    var whatsAppUrl = WhatsAppHelper.BuildWhatsAppUrl(
                        order.CustomerPhone,
                        WhatsAppHelper.BuildBillReceiptMessage(restName, bill.BillNumber, order.OrderNumber, order.Table?.TableNumber ?? "Table", bill.GrandTotal, dto.Method.ToString(), fullReceiptUrl),
                        forceWeb: true
                    );

                    return Ok(new
                    {
                        success = true,
                        billNumber = bill.BillNumber,
                        receiptUrl = fullReceiptUrl,
                        customerPhone = order.CustomerPhone,
                        whatsAppUrl = whatsAppUrl
                    });
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogWarning(ex, "Concurrency conflict settling bill for Order {OrderId}. Returning already paid result.", dto.OrderId);
                    return await GetAlreadyPaidResponseAsync(order.Id, baseUrl, restName, dto.Method);
                }
                catch (DbUpdateException ex) when (IsUniqueIndexViolation(ex))
                {
                    await transaction.RollbackAsync();
                    if (ex.InnerException?.Message.Contains("IX_Bills_BillNumber") == true && attempts < maxAttempts)
                    {
                        _logger.LogWarning("BillNumber collision detected. Retrying with new BillNumber (attempt {Attempt}).", attempts);
                        _context.ChangeTracker.Clear();
                        order = await _context.Orders
                            .IgnoreQueryFilters()
                            .Include(o => o.Restaurant)
                            .Include(o => o.Table)
                            .Include(o => o.Bill)
                            .FirstOrDefaultAsync(o => o.Id == dto.OrderId);
                        continue;
                    }

                    _logger.LogWarning(ex, "Duplicate bill detected for Order {OrderId}. Returning already paid result.", dto.OrderId);
                    return await GetAlreadyPaidResponseAsync(order.Id, baseUrl, restName, dto.Method);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Failed to settle bill for Order {OrderId}", dto.OrderId);
                    return StatusCode(500, new { message = "Payment processing failed." });
                }
            }

            return StatusCode(500, new { message = "Payment processing failed due to repeated number collisions." });
        }

        private static string GenerateBillNumber()
        {
            var suffix = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
            return $"BILL-{DateTime.UtcNow:yyMMdd}-{suffix}";
        }

        private static bool IsUniqueIndexViolation(DbUpdateException ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            return msg.Contains("IX_") || msg.Contains("UNIQUE") || msg.Contains("duplicate") || msg.Contains("2601") || msg.Contains("2627");
        }

        private async Task<IActionResult> GetAlreadyPaidResponseAsync(Guid orderId, string baseUrl, string restName, PaymentMethod method)
        {
            _context.ChangeTracker.Clear();
            var refreshedOrder = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Bill)
                .Include(o => o.Table)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            var billNum = refreshedOrder?.Bill?.BillNumber ?? "PAID";
            var receiptUrl = $"{baseUrl}/Billing/PrintReceipt?orderId={orderId}";
            var whatsAppUrl = WhatsAppHelper.BuildWhatsAppUrl(
                refreshedOrder?.CustomerPhone,
                WhatsAppHelper.BuildBillReceiptMessage(restName, billNum, refreshedOrder?.OrderNumber ?? "", refreshedOrder?.Table?.TableNumber ?? "Table", refreshedOrder?.Bill?.GrandTotal ?? 0, method.ToString(), receiptUrl),
                forceWeb: true
            );

            return Ok(new
            {
                success = true,
                alreadyPaid = true,
                billNumber = billNum,
                receiptUrl = receiptUrl,
                customerPhone = refreshedOrder?.CustomerPhone,
                whatsAppUrl = whatsAppUrl
            });
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> PrintReceipt(Guid orderId)
        {
            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Table)
                .Include(o => o.OrderItems)
                .Include(o => o.Bill)
                    .ThenInclude(b => b!.Payments)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null) return NotFound("Order not found.");

            var restaurant = await _context.Restaurants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == order.RestaurantId);

            ViewBag.Restaurant = restaurant;
            return View(order);
        }

        [HttpPost]
        public async Task<IActionResult> AcknowledgeServiceRequest([FromBody] Guid id)
        {
            var req = await _context.ServiceRequests.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == id);
            if (req == null) return NotFound(new { message = "Service request not found." });

            var currentTenantId = _tenantService.CurrentRestaurantId;
            if (!User.IsInRole(AppRoles.SuperAdmin) && (!currentTenantId.HasValue || req.RestaurantId != currentTenantId.Value))
            {
                return Forbid();
            }

            req.IsHandled = true;
            req.HandledAtUtc = DateTime.UtcNow;
            req.HandledBy = User.Identity?.Name ?? "Cashier";
            await _context.SaveChangesAsync();

            return Ok(new { success = true });
        }
    }
}
