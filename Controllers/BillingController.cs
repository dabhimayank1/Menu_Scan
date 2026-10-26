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

        public BillingController(
            ApplicationDbContext context,
            ITenantService tenantService,
            IHubContext<OrderHub> hubContext,
            ILogger<BillingController> logger)
        {
            _context = context;
            _tenantService = tenantService;
            _hubContext = hubContext;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var restaurantId = _tenantService.CurrentRestaurantId;
            if (!restaurantId.HasValue)
            {
                var defaultRest = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.IsActive);
                if (defaultRest == null) return NotFound("No active restaurant found.");
                restaurantId = defaultRest.Id;
            }

            // Fetch active orders requiring settlement
            var activeOrders = await _context.Orders
                .IgnoreQueryFilters()
                .Where(o => o.RestaurantId == restaurantId.Value && o.Status != OrderStatus.Completed && o.Status != OrderStatus.Cancelled)
                .OrderByDescending(o => o.CreatedAtUtc)
                .Include(o => o.Table)
                .Include(o => o.OrderItems)
                .Include(o => o.Bill)
                .ToListAsync();

            // Fetch recent settled bills (today)
            var todayUtc = DateTime.UtcNow.Date;
            var settledBills = await _context.Bills
                .IgnoreQueryFilters()
                .Where(b => b.RestaurantId == restaurantId.Value && b.Status == BillStatus.Paid && b.CreatedAtUtc >= todayUtc)
                .OrderByDescending(b => b.ClosedAtUtc)
                .Include(b => b.Order)
                    .ThenInclude(o => o!.Table)
                .Include(b => b.Payments)
                .Take(20)
                .ToListAsync();

            // Fetch unhandled service requests
            var pendingRequests = await _context.ServiceRequests
                .IgnoreQueryFilters()
                .Where(s => s.RestaurantId == restaurantId.Value && !s.IsHandled)
                .Include(s => s.Table)
                .OrderByDescending(s => s.CreatedAtUtc)
                .ToListAsync();

            ViewBag.RestaurantId = restaurantId.Value;
            ViewBag.SettledBills = settledBills;
            ViewBag.PendingRequests = pendingRequests;
            return View(activeOrders);
        }

        [HttpPost]
        public async Task<IActionResult> SettleBill([FromBody] SettleBillDto dto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new { message = "Invalid payment data." });
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
                        BillNumber = $"BILL-{DateTime.UtcNow:yyMMdd}-{Random.Shared.Next(1000, 9999)}",
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

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                // Notify via SignalR
                await _hubContext.Clients.Group($"Restaurant_{order.RestaurantId}")
                    .SendAsync("ReceiveOrderStatusUpdate", order.OrderNumber, "Completed", order.Table?.TableNumber ?? "");

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
                    .SendAsync("ReceiveOrderStatusUpdate", order.OrderNumber, "Completed", order.Table?.TableNumber ?? "");

                _logger.LogInformation("Bill {BillNumber} settled for Order {OrderNumber} amount {Amount} via {Method}",
                    bill.BillNumber, order.OrderNumber, bill.GrandTotal, dto.Method);

                return Ok(new
                {
                    success = true,
                    billNumber = bill.BillNumber,
                    receiptUrl = $"/Billing/PrintReceipt?orderId={order.Id}"
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to settle bill for Order {OrderId}", dto.OrderId);
                return StatusCode(500, new { message = "Payment processing failed." });
            }
        }

        [HttpGet]
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

            req.IsHandled = true;
            req.HandledAtUtc = DateTime.UtcNow;
            req.HandledBy = User.Identity?.Name ?? "Cashier";
            await _context.SaveChangesAsync();

            return Ok(new { success = true });
        }
    }
}
