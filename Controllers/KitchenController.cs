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
                if (User.IsInRole(AppRoles.SuperAdmin))
                {
                    return RedirectToAction("Index", "SuperAdmin");
                }
                return Forbid();
            }

            var cutoffUtc = DateTime.UtcNow.AddHours(-24);
            var orders = await _context.Orders
                .IgnoreQueryFilters()
                .Where(o => o.RestaurantId == restaurantId.Value 
                         && o.Status != OrderStatus.Completed 
                         && o.Status != OrderStatus.Cancelled
                         && o.CreatedAtUtc >= cutoffUtc)
                .OrderBy(o => o.CreatedAtUtc)
                .Include(o => o.Table)
                .Include(o => o.OrderItems)
                .ToListAsync();

            ViewBag.RestaurantId = restaurantId.Value;
            return View(orders);
        }

        [HttpPost]
        public async Task<IActionResult> UpdateStatus([FromBody] UpdateStatusDto? dto)
        {
            if (dto == null || !ModelState.IsValid)
            {
                return BadRequest(new { message = "Invalid status update request." });
            }

            if (!Enum.IsDefined(typeof(OrderStatus), dto.NewStatus))
            {
                return BadRequest(new { message = "Invalid or undefined order status value." });
            }

            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Table)
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

            // Kitchen cannot bypass billing to complete orders
            if (dto.NewStatus == OrderStatus.Completed)
            {
                return BadRequest(new { message = "Orders can only be marked Completed by the Cashier upon bill settlement." });
            }

            // If status is unchanged, skip writing status-history row and return OK
            if (order.Status == dto.NewStatus)
            {
                return Ok(new { success = true, newStatus = order.Status.ToString() });
            }

            // Validate transition using OrderStatusRules
            if (!OrderStatusRules.CanTransition(order.Status, dto.NewStatus, out var errorMsg))
            {
                return BadRequest(new { message = errorMsg ?? $"Cannot transition order from '{order.Status}' to '{dto.NewStatus}'." });
            }

            var oldStatus = order.Status;
            order.Status = dto.NewStatus;

            _context.OrderStatusHistories.Add(new OrderStatusHistory
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
        [System.ComponentModel.DataAnnotations.Required]
        public Guid OrderId { get; set; }
        public OrderStatus NewStatus { get; set; }
    }
}
