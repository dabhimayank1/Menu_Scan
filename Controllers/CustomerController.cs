using MenuScan.Data;
using MenuScan.Hubs;
using MenuScan.Models.Entities;
using MenuScan.Models.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MenuScan.Controllers
{
    public class CustomerController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<OrderHub> _hubContext;
        private readonly ILogger<CustomerController> _logger;

        public CustomerController(
            ApplicationDbContext context,
            IHubContext<OrderHub> hubContext,
            ILogger<CustomerController> logger)
        {
            _context = context;
            _hubContext = hubContext;
            _logger = logger;
        }

        [HttpGet("r/{slug}/t/{token}")]
        public async Task<IActionResult> TableMenu(string slug, string token)
        {
            var restaurant = await _context.Restaurants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Slug == slug && r.IsActive);

            if (restaurant == null)
            {
                return NotFound("Restaurant not found or inactive.");
            }

            var table = await _context.DiningTables
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.RestaurantId == restaurant.Id && t.Token == token && t.IsActive);

            if (table == null)
            {
                return NotFound("Table QR code is invalid or table is inactive.");
            }

            var categories = await _context.Categories
                .IgnoreQueryFilters()
                .Where(c => c.RestaurantId == restaurant.Id && c.IsActive)
                .OrderBy(c => c.DisplayOrder)
                .Include(c => c.MenuItems.Where(m => m.IsAvailable).OrderBy(m => m.DisplayOrder))
                    .ThenInclude(m => m.Customizations.Where(cu => cu.IsAvailable))
                .ToListAsync();

            var viewModel = new CustomerMenuViewModel
            {
                Restaurant = restaurant,
                Table = table,
                Categories = categories
            };

            return View(viewModel);
        }

        [HttpPost("api/orders/submit")]
        [IgnoreAntiforgeryToken] // Allow API fetch call from mobile cart
        public async Task<IActionResult> SubmitOrder([FromBody] OrderSubmissionDto dto)
        {
            if (!ModelState.IsValid || dto.Items == null || !dto.Items.Any())
            {
                return BadRequest(new { message = "Invalid order submission. Cart cannot be empty." });
            }

            // 1. Check Idempotency to prevent duplicate submissions
            var existingOrder = await _context.Orders
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.IdempotencyKey == dto.IdempotencyKey);

            if (existingOrder != null)
            {
                return Ok(new
                {
                    success = true,
                    isDuplicate = true,
                    orderNumber = existingOrder.OrderNumber,
                    orderToken = existingOrder.OrderToken,
                    redirectUrl = $"/order/track/{existingOrder.OrderToken}"
                });
            }

            // 2. Validate Restaurant & Table
            var restaurant = await _context.Restaurants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Slug == dto.RestaurantSlug && r.IsActive);

            if (restaurant == null)
            {
                return BadRequest(new { message = "Restaurant not found." });
            }

            var table = await _context.DiningTables
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.RestaurantId == restaurant.Id && t.Token == dto.TableToken && t.IsActive);

            if (table == null)
            {
                return BadRequest(new { message = "Table verification failed." });
            }

            // 3. Validate Menu Items & Authoritative Prices with Customizations
            var itemIds = dto.Items.Select(i => i.MenuItemId).Distinct().ToList();
            var menuItems = await _context.MenuItems
                .IgnoreQueryFilters()
                .Include(m => m.Customizations)
                .Where(m => itemIds.Contains(m.Id) && m.RestaurantId == restaurant.Id && m.IsAvailable)
                .ToDictionaryAsync(m => m.Id);

            if (menuItems.Count != itemIds.Count)
            {
                return BadRequest(new { message = "Some selected items are no longer available. Please refresh your menu." });
            }

            // 4. Build Order with Authoritative Price & Customization Snapshots
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var order = new Order
                {
                    RestaurantId = restaurant.Id,
                    TableId = table.Id,
                    OrderNumber = $"ORD-{DateTime.UtcNow:yyMMdd}-{Random.Shared.Next(1000, 9999)}",
                    Status = OrderStatus.New,
                    CustomerNotes = dto.CustomerNotes,
                    IdempotencyKey = dto.IdempotencyKey,
                    TaxRate = restaurant.DefaultTaxRate
                };

                decimal subTotal = 0;
                foreach (var reqItem in dto.Items)
                {
                    if (reqItem.Quantity <= 0) continue;

                    var menuItem = menuItems[reqItem.MenuItemId];
                    decimal unitPrice = menuItem.Price;
                    var selectedCustomizationNames = new List<string>();

                    if (reqItem.CustomizationIds != null && reqItem.CustomizationIds.Any())
                    {
                        var validCustomizations = menuItem.Customizations
                            .Where(c => reqItem.CustomizationIds.Contains(c.Id) && c.IsAvailable)
                            .ToList();

                        foreach (var custom in validCustomizations)
                        {
                            unitPrice += custom.Price;
                            selectedCustomizationNames.Add(custom.Name);
                        }
                    }

                    var customizationSummary = selectedCustomizationNames.Any() 
                        ? string.Join(", ", selectedCustomizationNames) 
                        : reqItem.CustomizationSummary;

                    var itemTotal = unitPrice * reqItem.Quantity;
                    subTotal += itemTotal;

                    order.OrderItems.Add(new OrderItem
                    {
                        MenuItemId = menuItem.Id,
                        ItemNameSnapshot = menuItem.Name,
                        UnitPriceSnapshot = unitPrice,
                        Quantity = reqItem.Quantity,
                        TotalPrice = itemTotal,
                        CustomizationSummary = customizationSummary
                    });
                }

                order.SubTotal = subTotal;
                order.TaxAmount = Math.Round(subTotal * (order.TaxRate / 100m), 2);
                order.TotalAmount = order.SubTotal + order.TaxAmount;

                // Add Status History
                order.StatusHistory.Add(new OrderStatusHistory
                {
                    OldStatus = OrderStatus.New,
                    NewStatus = OrderStatus.New,
                    ChangedBy = "Customer (Table QR)",
                    Notes = $"Order placed at {table.TableNumber}"
                });

                _context.Orders.Add(order);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                // 5. Broadcast to Kitchen & Cashier via SignalR
                var orderPayload = new
                {
                    orderId = order.Id,
                    orderNumber = order.OrderNumber,
                    tableNumber = table.TableNumber,
                    totalAmount = order.TotalAmount,
                    itemCount = order.OrderItems.Sum(i => i.Quantity),
                    items = order.OrderItems.Select(i => new { name = i.ItemNameSnapshot, qty = i.Quantity, customizations = i.CustomizationSummary }).ToList(),
                    createdAt = order.CreatedAtUtc.ToString("HH:mm:ss"),
                    status = order.Status.ToString(),
                    notes = order.CustomerNotes
                };

                await _hubContext.Clients.Group($"Restaurant_{restaurant.Id}")
                    .SendAsync("ReceiveNewOrder", orderPayload);

                _logger.LogInformation("Order {OrderNumber} created successfully for {Restaurant} {TableNumber}",
                    order.OrderNumber, restaurant.Name, table.TableNumber);

                return Ok(new
                {
                    success = true,
                    orderNumber = order.OrderNumber,
                    orderToken = order.OrderToken,
                    redirectUrl = $"/order/track/{order.OrderToken}"
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to create order for {RestaurantSlug}", dto.RestaurantSlug);
                return StatusCode(500, new { message = "An error occurred while creating your order. Please try again." });
            }
        }

        [HttpGet("order/track/{token}")]
        public async Task<IActionResult> TrackOrder(string token)
        {
            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Table)
                .Include(o => o.OrderItems)
                .Include(o => o.StatusHistory.OrderByDescending(s => s.ChangedAtUtc))
                .FirstOrDefaultAsync(o => o.OrderToken == token);

            if (order == null)
            {
                return NotFound("Order tracking token not found.");
            }

            var restaurant = await _context.Restaurants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == order.RestaurantId);

            var viewModel = new OrderTrackingViewModel
            {
                Order = order,
                Restaurant = restaurant!,
                Table = order.Table!
            };

            return View(viewModel);
        }
    }
}
