using MenuScan.Data;
using MenuScan.Hubs;
using MenuScan.Models.Entities;
using MenuScan.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MenuScan.Controllers
{
    [Authorize(Roles = $"{AppRoles.KitchenStaff},{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
    public class KitchenController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ITenantService _tenantService;
        private readonly IHubContext<OrderHub> _hubContext;
        private readonly ILogger<KitchenController> _logger;

        public KitchenController(
            ApplicationDbContext context,
            ITenantService tenantService,
            IHubContext<OrderHub> hubContext,
            ILogger<KitchenController> logger)
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
                // Fallback for SuperAdmin: choose first active restaurant
                var defaultRest = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.IsActive);
                if (defaultRest == null) return NotFound("No active restaurant found.");
                restaurantId = defaultRest.Id;
            }

            var orders = await _context.Orders
                .IgnoreQueryFilters()
                .Where(o => o.RestaurantId == restaurantId.Value && o.Status != OrderStatus.Completed && o.Status != OrderStatus.Cancelled)
                .OrderBy(o => o.CreatedAtUtc)
                .Include(o => o.Table)
                .Include(o => o.OrderItems)
                .ToListAsync();

            ViewBag.RestaurantId = restaurantId.Value;
            return View(orders);
        }

        [HttpPost]
        public async Task<IActionResult> UpdateStatus([FromBody] UpdateStatusDto dto)
        {
            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Table)
                .FirstOrDefaultAsync(o => o.Id == dto.OrderId);

            if (order == null)
            {
                return NotFound(new { message = "Order not found." });
            }

            var oldStatus = order.Status;
            order.Status = dto.NewStatus;

            order.StatusHistory.Add(new OrderStatusHistory
            {
                OrderId = order.Id,
                OldStatus = oldStatus,
                NewStatus = dto.NewStatus,
                ChangedBy = User.Identity?.Name ?? "Kitchen Staff",
                Notes = $"Status updated to {dto.NewStatus}"
            });

            await _context.SaveChangesAsync();

            // Broadcast to Restaurant Group (Kitchen & Cashier)
            await _hubContext.Clients.Group($"Restaurant_{order.RestaurantId}")
                .SendAsync("ReceiveOrderStatusUpdate", order.OrderNumber, order.Status.ToString(), order.Table?.TableNumber ?? "Unknown");

            // Broadcast to Customer Order Group
            await _hubContext.Clients.Group($"Order_{order.OrderToken}")
                .SendAsync("ReceiveOrderStatusUpdate", order.OrderNumber, order.Status.ToString(), order.Table?.TableNumber ?? "Unknown");

            _logger.LogInformation("Order {OrderNumber} status changed from {Old} to {New} by {User}",
                order.OrderNumber, oldStatus, dto.NewStatus, User.Identity?.Name);

            return Ok(new { success = true, newStatus = order.Status.ToString() });
        }
    }

    public class UpdateStatusDto
    {
        public Guid OrderId { get; set; }
        public OrderStatus NewStatus { get; set; }
    }
}
