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
    [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.Cashier},{AppRoles.KitchenStaff},{AppRoles.SuperAdmin}")]
    public class FloorPlanController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ITenantService _tenantService;
        private readonly IHubContext<OrderHub> _hubContext;
        private readonly ILogger<FloorPlanController> _logger;

        public FloorPlanController(
            ApplicationDbContext context,
            ITenantService tenantService,
            IHubContext<OrderHub> hubContext,
            ILogger<FloorPlanController> logger)
        {
            _context = context;
            _tenantService = tenantService;
            _hubContext = hubContext;
            _logger = logger;
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
            if (r == null) return (null, NotFound("Restaurant not configured."));
            return (r, null);
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var (restaurant, errorResult) = await GetCurrentRestaurantAsync();
            if (errorResult != null) return errorResult;

            var tables = await _context.DiningTables
                .IgnoreQueryFilters()
                .Where(t => t.RestaurantId == restaurant!.Id && t.IsActive)
                .OrderBy(t => t.TableNumber)
                .ToListAsync();

            var activeOrders = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.OrderItems)
                .Where(o => o.RestaurantId == restaurant.Id &&
                            o.Status != OrderStatus.Cancelled &&
                            o.Status != OrderStatus.Completed &&
                            o.CreatedAtUtc >= DateTime.UtcNow.AddHours(-24))
                .ToListAsync();

            var pendingRequests = await _context.ServiceRequests
                .IgnoreQueryFilters()
                .Where(s => s.RestaurantId == restaurant.Id && !s.IsHandled)
                .ToListAsync();

            var tableDtos = new List<FloorTableDto>();

            foreach (var table in tables)
            {
                var tableOrder = activeOrders
                    .Where(o => o.TableId == table.Id)
                    .OrderByDescending(o => o.CreatedAtUtc)
                    .FirstOrDefault();

                var tableRequests = pendingRequests
                    .Where(s => s.TableId == table.Id)
                    .OrderByDescending(s => s.CreatedAtUtc)
                    .ToList();

                var hasFoodReady = tableOrder != null && tableOrder.Status == OrderStatus.Ready;
                var hasBillCall = tableRequests.Any(r => r.RequestType.Equals("Bill", StringComparison.OrdinalIgnoreCase));
                var hasAssistanceCall = tableRequests.Any(r => !r.RequestType.Equals("Bill", StringComparison.OrdinalIgnoreCase));

                FloorTableStatus status;
                if (hasBillCall)
                {
                    status = FloorTableStatus.BillRequested;
                }
                else if (hasAssistanceCall)
                {
                    status = FloorTableStatus.AssistanceCalled;
                }
                else if (hasFoodReady)
                {
                    status = FloorTableStatus.FoodReady;
                }
                else if (tableOrder != null)
                {
                    status = tableOrder.Status switch
                    {
                        OrderStatus.New => FloorTableStatus.OccupiedNew,
                        OrderStatus.Preparing or OrderStatus.Accepted => FloorTableStatus.OccupiedPreparing,
                        OrderStatus.Served => FloorTableStatus.OccupiedServed,
                        _ => FloorTableStatus.OccupiedServed
                    };
                }
                else
                {
                    status = FloorTableStatus.Available;
                }

                var seatedMinutes = tableOrder != null
                    ? (int)Math.Max(0, (DateTime.UtcNow - tableOrder.CreatedAtUtc).TotalMinutes)
                    : 0;

                tableDtos.Add(new FloorTableDto
                {
                    TableId = table.Id,
                    TableNumber = table.TableNumber,
                    Capacity = table.Capacity,
                    Token = table.Token,
                    FloorStatus = status,
                    HasFoodReady = hasFoodReady,
                    HasAssistanceCall = hasAssistanceCall,
                    HasBillCall = hasBillCall,
                    ActiveOrderId = tableOrder?.Id,
                    ActiveOrderNumber = tableOrder?.OrderNumber,
                    ActiveOrderNotes = tableOrder?.CustomerNotes,
                    CustomerName = tableOrder?.CustomerName,
                    ActiveOrderStatus = tableOrder?.Status,
                    ActiveOrderTotal = tableOrder?.TotalAmount ?? 0,
                    OrderCreatedAtUtc = tableOrder?.CreatedAtUtc,
                    SeatedDurationMinutes = seatedMinutes,
                    OrderItems = tableOrder?.OrderItems.Select(i => new FloorOrderItemDto
                    {
                        ItemName = i.ItemNameSnapshot,
                        Quantity = i.Quantity,
                        Price = i.TotalPrice,
                        CustomizationSummary = i.CustomizationSummary
                    }).ToList() ?? new List<FloorOrderItemDto>(),
                    PendingRequests = tableRequests.Select(r => new FloorServiceRequestDto
                    {
                        RequestId = r.Id,
                        RequestType = r.RequestType,
                        Notes = r.Note,
                        CreatedAtUtc = r.CreatedAtUtc,
                        TimeAgo = GetTimeAgo(r.CreatedAtUtc)
                    }).ToList()
                });
            }

            var vm = new FloorPlanViewModel
            {
                Restaurant = restaurant,
                Tables = tableDtos
            };

            return View(vm);
        }

        [HttpPost("api/floor/acknowledge-service/{requestId}")]
        public async Task<IActionResult> AcknowledgeService(Guid requestId)
        {
            var req = await _context.ServiceRequests
                .IgnoreQueryFilters()
                .Include(s => s.Table)
                .FirstOrDefaultAsync(s => s.Id == requestId);

            if (req == null) return NotFound(new { success = false, message = "Request not found." });

            var currentTenantId = _tenantService.CurrentRestaurantId;
            if (!User.IsInRole(AppRoles.SuperAdmin) && (!currentTenantId.HasValue || req.RestaurantId != currentTenantId.Value))
            {
                return Forbid();
            }

            req.IsHandled = true;
            req.HandledAtUtc = DateTime.UtcNow;
            req.HandledBy = User.Identity?.Name ?? "Staff";

            await _context.SaveChangesAsync();

            await _hubContext.Clients.Group($"Restaurant_{req.RestaurantId}")
                .SendAsync("ReceiveWaiterCallHandled", new
                {
                    requestId = req.Id,
                    tableNumber = req.Table?.TableNumber ?? "Unknown"
                });

            return Ok(new { success = true });
        }

        [HttpPost("api/floor/mark-served/{orderId}")]
        public async Task<IActionResult> MarkServed(Guid orderId)
        {
            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Table)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null) return NotFound(new { success = false, message = "Order not found." });

            var currentTenantId = _tenantService.CurrentRestaurantId;
            if (!User.IsInRole(AppRoles.SuperAdmin) && (!currentTenantId.HasValue || order.RestaurantId != currentTenantId.Value))
            {
                return Forbid();
            }

            // If status is unchanged, skip writing status history
            if (order.Status == OrderStatus.Served)
            {
                return Ok(new { success = true, message = "Order is already marked as Served." });
            }

            // Validate transition using OrderStatusRules (Only Ready -> Served is allowed; Completed/Cancelled/New etc. are rejected with 400)
            if (!OrderStatusRules.CanTransition(order.Status, OrderStatus.Served, out var errorMsg))
            {
                return BadRequest(new { success = false, message = errorMsg ?? $"Cannot mark order as Served from status '{order.Status}'." });
            }

            var oldStatus = order.Status;
            order.Status = OrderStatus.Served;

            _context.OrderStatusHistories.Add(new OrderStatusHistory
            {
                OrderId = order.Id,
                OldStatus = oldStatus,
                NewStatus = OrderStatus.Served,
                ChangedBy = User.Identity?.Name ?? "Staff Runner",
                ChangedAtUtc = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            await _hubContext.Clients.Group($"Restaurant_{order.RestaurantId}")
                .SendAsync("ReceiveOrderStatusUpdate", order.OrderNumber, "Served", order.Table?.TableNumber ?? "Unknown", order.Id.ToString());

            await _hubContext.Clients.Group($"Order_{order.OrderToken}")
                .SendAsync("ReceiveOrderStatusUpdate", order.OrderNumber, "Served", order.Table?.TableNumber ?? "Unknown", order.Id.ToString());

            _logger.LogInformation("Order {OrderNumber} marked Served by {Staff}", order.OrderNumber, User.Identity?.Name);

            return Ok(new { success = true });
        }

        [HttpPost("api/floor/clean-table/{tableId}")]
        public async Task<IActionResult> CleanTable(Guid tableId)
        {
            var table = await _context.DiningTables
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tableId);

            if (table == null) return NotFound(new { success = false, message = "Table not found." });

            var currentTenantId = _tenantService.CurrentRestaurantId;
            if (!User.IsInRole(AppRoles.SuperAdmin) && (!currentTenantId.HasValue || table.RestaurantId != currentTenantId.Value))
            {
                return Forbid();
            }

            // Mark any remaining pending service calls as handled
            var pendingRequests = await _context.ServiceRequests
                .IgnoreQueryFilters()
                .Where(s => s.TableId == tableId && !s.IsHandled)
                .ToListAsync();

            foreach (var r in pendingRequests)
            {
                r.IsHandled = true;
                r.HandledAtUtc = DateTime.UtcNow;
                r.HandledBy = User.Identity?.Name ?? "Staff";
            }

            await _context.SaveChangesAsync();

            await _hubContext.Clients.Group($"Restaurant_{table.RestaurantId}")
                .SendAsync("ReceiveTableCleaned", new
                {
                    tableId = table.Id,
                    tableNumber = table.TableNumber
                });

            return Ok(new { success = true });
        }

        private static string GetTimeAgo(DateTime utcTime)
        {
            var span = DateTime.UtcNow - utcTime;
            if (span.TotalMinutes < 1) return "Just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            return $"{(int)span.TotalHours}h ago";
        }
    }
}
