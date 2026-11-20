using MenuScan.Data;
using MenuScan.Hubs;
using MenuScan.Models.Entities;
using MenuScan.Models.ViewModels;
using MenuScan.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MenuScan.Controllers
{
    public class CustomerController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<OrderHub> _hubContext;
        private readonly IQRService _qrService;
        private readonly ILogger<CustomerController> _logger;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _configuration;
        private readonly ITimeHelper _timeHelper;
        private readonly ITenantService _tenantService;

        public CustomerController(
            ApplicationDbContext context,
            IHubContext<OrderHub> hubContext,
            IQRService qrService,
            ILogger<CustomerController> logger,
            IWebHostEnvironment env,
            IConfiguration configuration,
            ITimeHelper timeHelper,
            ITenantService tenantService)
        {
            _context = context;
            _hubContext = hubContext;
            _qrService = qrService;
            _logger = logger;
            _env = env;
            _configuration = configuration;
            _timeHelper = timeHelper;
            _tenantService = tenantService;
        }

        [HttpGet("r/{slug}/t/{token}")]
        public async Task<IActionResult> TableMenu(string slug, string token)
        {
            var restaurant = await _context.Restaurants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Slug == slug && r.IsActive);

            if (restaurant == null)
            {
                return NotFound("Restaurant not found or inactive.");
            }

            var table = await _context.DiningTables
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.RestaurantId == restaurant.Id && t.Token == token && t.IsActive);

            if (table == null)
            {
                return NotFound("Table QR code is invalid or table is inactive.");
            }

            var categories = await _context.Categories
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => c.RestaurantId == restaurant.Id && c.IsActive)
                .OrderBy(c => c.DisplayOrder)
                .Include(c => c.MenuItems.Where(m => m.IsAvailable).OrderBy(m => m.DisplayOrder))
                    .ThenInclude(m => m.Customizations.Where(cu => cu.IsAvailable))
                .AsSplitQuery()
                .ToListAsync();

            var viewModel = new CustomerMenuViewModel
            {
                Restaurant = restaurant,
                Table = table,
                Categories = categories,
                ActiveOrders = new List<Order>() // Privacy: never leak other diners' orders in server HTML; client loads own orders via API using local tokens
            };

            return View(viewModel);
        }

        [HttpGet("api/table/active-orders")]
        [EnableRateLimiting("diner-api-policy")]
        public async Task<IActionResult> GetTableActiveOrders(
            [FromQuery] string tableToken, 
            [FromQuery] string restaurantSlug,
            [FromQuery] string? tokens = null)
        {
            // Privacy protection: if tokens query param is missing or empty, return empty list
            if (string.IsNullOrWhiteSpace(tokens))
            {
                return Ok(Array.Empty<object>());
            }

            const int MaxTokensAllowed = 20;
            var tokenList = tokens.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxTokensAllowed)
                .ToList();

            if (!tokenList.Any())
            {
                return Ok(Array.Empty<object>());
            }

            var restaurant = await _context.Restaurants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Slug == restaurantSlug && r.IsActive);
            if (restaurant == null) return NotFound();

            var table = await _context.DiningTables.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.RestaurantId == restaurant.Id && t.Token == tableToken && t.IsActive);
            if (table == null) return NotFound();

            var cutoffUtc = DateTime.UtcNow.AddHours(-24);
            var query = _context.Orders.IgnoreQueryFilters()
                .Include(o => o.OrderItems)
                .Include(o => o.Bill)
                .Where(o => o.RestaurantId == restaurant.Id 
                         && o.TableId == table.Id 
                         && tokenList.Contains(o.OrderToken)
                         && o.Status != OrderStatus.Cancelled 
                         && o.Status != OrderStatus.Completed 
                         && (o.Bill == null || o.Bill.Status != BillStatus.Paid)
                         && o.CreatedAtUtc >= cutoffUtc);

            var orders = await query
                .OrderByDescending(o => o.CreatedAtUtc)
                .Take(MaxTokensAllowed)
                .Select(o => new
                {
                    orderId = o.Id,
                    orderNumber = o.OrderNumber,
                    orderToken = o.OrderToken,
                    status = o.Status.ToString(),
                    statusCode = (int)o.Status,
                    totalAmount = o.TotalAmount,
                    subTotal = o.SubTotal,
                    taxAmount = o.TaxAmount,
                    itemCount = o.OrderItems.Sum(i => i.Quantity),
                    items = o.OrderItems.Select(i => new { name = i.ItemNameSnapshot, qty = i.Quantity, price = i.TotalPrice, custom = i.CustomizationSummary }).ToList(),
                    createdAt = _timeHelper.Format(o.CreatedAtUtc, "hh:mm tt"),
                    notes = o.CustomerNotes,
                    customerName = o.CustomerName,
                    isPaid = o.Status == OrderStatus.Completed || (o.Bill != null && o.Bill.Status == BillStatus.Paid),
                    billNumber = o.Bill != null ? o.Bill.BillNumber : null
                })
                .ToListAsync();

            return Ok(orders);
        }

        [HttpPost("api/orders/submit")]
        [IgnoreAntiforgeryToken] // Allow API fetch call from mobile cart
        [EnableRateLimiting("diner-api-policy")]
        public async Task<IActionResult> SubmitOrder([FromBody] OrderSubmissionDto? dto)
        {
            if (dto == null || dto.Items == null || !dto.Items.Any() || dto.Items.All(i => i.Quantity <= 0))
            {
                return BadRequest(new { message = "Invalid order submission. Cart cannot be empty." });
            }

            if (!string.IsNullOrWhiteSpace(dto.CustomerName) && dto.CustomerName.Trim().Length > 100)
            {
                return BadRequest(new { message = "Customer name cannot exceed 100 characters." });
            }

            if (!string.IsNullOrWhiteSpace(dto.CustomerNotes) && dto.CustomerNotes.Trim().Length > 500)
            {
                return BadRequest(new { message = "Customer notes cannot exceed 500 characters." });
            }

            if (!string.IsNullOrWhiteSpace(dto.CustomerPhone))
            {
                var trimmedPhone = dto.CustomerPhone.Trim();
                if (trimmedPhone.Length > 25 || !System.Text.RegularExpressions.Regex.IsMatch(trimmedPhone, @"^[\d\+\s\-\(\)]+$"))
                {
                    return BadRequest(new { message = "Invalid phone number format." });
                }
                var digitCount = trimmedPhone.Count(char.IsDigit);
                if (digitCount < 7 || digitCount > 15)
                {
                    return BadRequest(new { message = "Phone number must contain between 7 and 15 digits." });
                }
            }

            if (!ModelState.IsValid)
            {
                var firstError = ModelState.Values.SelectMany(v => v.Errors).FirstOrDefault()?.ErrorMessage ?? "Invalid order submission.";
                return BadRequest(new { message = firstError });
            }

            // 1. Validate Restaurant & Table
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

            // 2. Check Idempotency within restaurant to prevent duplicate submissions
            var existingOrder = !string.IsNullOrWhiteSpace(dto.IdempotencyKey)
                ? await _context.Orders
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(o => o.RestaurantId == restaurant.Id && o.IdempotencyKey == dto.IdempotencyKey)
                : null;

            if (existingOrder != null)
            {
                return Ok(new
                {
                    success = true,
                    isDuplicate = true,
                    orderId = existingOrder.Id,
                    orderNumber = existingOrder.OrderNumber,
                    orderToken = existingOrder.OrderToken,
                    redirectUrl = $"/order/track/{existingOrder.OrderToken}"
                });
            }

            // 3. Validate Menu Items & Authoritative Prices with Customizations
            var validItems = dto.Items.Where(i => i.Quantity > 0).ToList();
            var itemIds = validItems.Select(i => i.MenuItemId).Distinct().ToList();
            var menuItems = await _context.MenuItems
                .IgnoreQueryFilters()
                .Include(m => m.Customizations)
                .Where(m => itemIds.Contains(m.Id) && m.RestaurantId == restaurant.Id && m.IsAvailable)
                .ToDictionaryAsync(m => m.Id);

            if (menuItems.Count != itemIds.Count)
            {
                return BadRequest(new { message = "Some selected items are no longer available. Please refresh your menu." });
            }

            int attempts = 0;
            const int maxAttempts = 2;

            while (attempts < maxAttempts)
            {
                attempts++;
                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    var order = new Order
                    {
                        RestaurantId = restaurant.Id,
                        TableId = table.Id,
                        OrderNumber = GenerateOrderNumber(),
                        Status = OrderStatus.New,
                        CustomerNotes = dto.CustomerNotes?.Trim(),
                        CustomerName = dto.CustomerName?.Trim(),
                        CustomerPhone = dto.CustomerPhone?.Trim(),
                        IdempotencyKey = dto.IdempotencyKey,
                        TaxRate = restaurant.DefaultTaxRate
                    };

                    decimal subTotal = 0;
                    foreach (var reqItem in validItems)
                    {
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

                    // Broadcast to Kitchen & Cashier via SignalR
                    var orderPayload = new
                    {
                        orderId = order.Id,
                        orderNumber = order.OrderNumber,
                        tableNumber = table.TableNumber,
                        totalAmount = order.TotalAmount,
                        itemCount = order.OrderItems.Sum(i => i.Quantity),
                        items = order.OrderItems.Select(i => new { name = i.ItemNameSnapshot, qty = i.Quantity, customizations = i.CustomizationSummary }).ToList(),
                        createdAt = _timeHelper.Format(order.CreatedAtUtc, "hh:mm tt"),
                        status = order.Status.ToString(),
                        notes = order.CustomerNotes,
                        customerName = order.CustomerName,
                        customerPhone = order.CustomerPhone,
                        orderToken = order.OrderToken
                    };

                    await _hubContext.Clients.Group($"Restaurant_{restaurant.Id}")
                        .SendAsync("ReceiveNewOrder", orderPayload);

                    _logger.LogInformation("Order {OrderNumber} created successfully for {Restaurant} {TableNumber}",
                        order.OrderNumber, restaurant.Name, table.TableNumber);

                    return Ok(new
                    {
                        success = true,
                        orderId = order.Id,
                        orderNumber = order.OrderNumber,
                        orderToken = order.OrderToken,
                        redirectUrl = $"/order/track/{order.OrderToken}"
                    });
                }
                catch (DbUpdateException ex) when (IsUniqueIndexViolation(ex))
                {
                    await transaction.RollbackAsync();
                    _context.ChangeTracker.Clear();

                    // If OrderNumber collided, retry once
                    if (ex.InnerException?.Message.Contains("IX_Orders_OrderNumber") == true && attempts < maxAttempts)
                    {
                        _logger.LogWarning("OrderNumber collision detected. Retrying with fresh OrderNumber.");
                        continue;
                    }

                    // If duplicate IdempotencyKey (e.g. concurrent submission), return existing order
                    var raceOrder = await _context.Orders
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(o => o.RestaurantId == restaurant.Id && o.IdempotencyKey == dto.IdempotencyKey);

                    if (raceOrder != null)
                    {
                        _logger.LogInformation("Concurrent duplicate submission caught by unique index for IdempotencyKey {Key}.", dto.IdempotencyKey);
                        return Ok(new
                        {
                            success = true,
                            isDuplicate = true,
                            orderNumber = raceOrder.OrderNumber,
                            orderToken = raceOrder.OrderToken,
                            redirectUrl = $"/order/track/{raceOrder.OrderToken}"
                        });
                    }

                    throw;
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Failed to create order for {RestaurantSlug}", dto.RestaurantSlug);
                    return StatusCode(500, new { message = "An error occurred while creating your order. Please try again." });
                }
            }

            return StatusCode(500, new { message = "An error occurred while creating your order. Please try again." });
        }

        private static string GenerateOrderNumber()
        {
            var suffix = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
            return $"ORD-{DateTime.UtcNow:yyMMdd}-{suffix}";
        }

        private static bool IsUniqueIndexViolation(DbUpdateException ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            return msg.Contains("IX_") || msg.Contains("UNIQUE") || msg.Contains("duplicate") || msg.Contains("2601") || msg.Contains("2627");
        }

        [HttpGet("order/track/{token}")]
        public async Task<IActionResult> TrackOrder(string token)
        {
            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Table)
                .Include(o => o.OrderItems)
                .Include(o => o.Bill)
                    .ThenInclude(b => b!.Payments)
                .Include(o => o.StatusHistory.OrderByDescending(s => s.ChangedAtUtc))
                .FirstOrDefaultAsync(o => o.OrderToken == token);

            if (order == null)
            {
                return NotFound("Order tracking token not found.");
            }

            var restaurant = await _context.Restaurants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == order.RestaurantId);

            var hasFeedback = await _context.CustomerFeedbacks
                .IgnoreQueryFilters()
                .AnyAsync(f => f.OrderId == order.Id);

            var baseUrl = NetworkHelper.ResolveBaseUrl(Request, _configuration);
            var trackerUrl = $"{baseUrl}/order/track/{order.OrderToken}";
            var restName = restaurant?.Name ?? "Brew & Bliss Coffee House";

            ViewBag.TrackerUrl = trackerUrl;
            ViewBag.Host = baseUrl;
            ViewBag.WhatsAppOrderConfirmedUrl = WhatsAppHelper.BuildWhatsAppUrl(
                order.CustomerPhone,
                WhatsAppHelper.BuildOrderConfirmedMessage(restName, order.OrderNumber, order.Table?.TableNumber ?? "Table", order.TotalAmount, trackerUrl)
            );

            if (order.Bill != null)
            {
                var receiptUrl = $"{baseUrl}/Billing/PrintReceipt?orderId={order.Id}&token={order.OrderToken}";
                var payMethod = order.Bill.Payments.FirstOrDefault()?.Method.ToString() ?? "Settled";
                ViewBag.WhatsAppReceiptUrl = WhatsAppHelper.BuildWhatsAppUrl(
                    order.CustomerPhone,
                    WhatsAppHelper.BuildBillReceiptMessage(restName, order.Bill.BillNumber, order.OrderNumber, order.Table?.TableNumber ?? "Table", order.Bill.GrandTotal, payMethod, receiptUrl)
                );
            }

            var pendingUpiRequest = await _context.ServiceRequests
                .IgnoreQueryFilters()
                .Where(s => s.RestaurantId == order.RestaurantId 
                         && (s.OrderId == order.Id || (s.OrderId == null && s.TableId == order.TableId))
                         && s.RequestType == "UPI Payment" 
                         && !s.IsHandled)
                .OrderByDescending(s => s.CreatedAtUtc)
                .FirstOrDefaultAsync();

            ViewBag.AwaitingCashierApproval = pendingUpiRequest != null;

            var viewModel = new OrderTrackingViewModel
            {
                Order = order,
                Restaurant = restaurant!,
                Table = order.Table!,
                HasSubmittedFeedback = hasFeedback
            };

            return View(viewModel);
        }

        [HttpPost("api/order/save-customer-contact")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> SaveCustomerContact([FromBody] UpdateCustomerContactDto? dto)
        {
            if (dto == null || !ModelState.IsValid || string.IsNullOrWhiteSpace(dto.OrderToken))
                return BadRequest(new { message = "Order token is required." });

            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Table)
                .Include(o => o.Restaurant)
                .Include(o => o.Bill)
                    .ThenInclude(b => b!.Payments)
                .FirstOrDefaultAsync(o => o.OrderToken == dto.OrderToken);

            if (order == null) return NotFound(new { message = "Order not found." });

            if (!string.IsNullOrWhiteSpace(dto.CustomerPhone))
            {
                var trimmed = dto.CustomerPhone.Trim();
                if (trimmed.Length > 25) return BadRequest(new { message = "Customer phone exceeds maximum length." });
                order.CustomerPhone = trimmed;
            }
            if (!string.IsNullOrWhiteSpace(dto.CustomerName))
            {
                var trimmed = dto.CustomerName.Trim();
                if (trimmed.Length > 100) return BadRequest(new { message = "Customer name exceeds maximum length." });
                order.CustomerName = trimmed;
            }

            await _context.SaveChangesAsync();

            var baseUrl = NetworkHelper.ResolveBaseUrl(Request, _configuration);
            var trackerUrl = $"{baseUrl}/order/track/{order.OrderToken}";
            var restName = order.Restaurant?.Name ?? "Brew & Bliss Coffee House";
            var wpOrderUrl = WhatsAppHelper.BuildWhatsAppUrl(
                order.CustomerPhone,
                WhatsAppHelper.BuildOrderConfirmedMessage(restName, order.OrderNumber, order.Table?.TableNumber ?? "Table", order.TotalAmount, trackerUrl)
            );

            string? wpReceiptUrl = null;
            if (order.Bill != null)
            {
                var receiptUrl = $"{baseUrl}/Billing/PrintReceipt?orderId={order.Id}&token={order.OrderToken}";
                var payMethod = order.Bill.Payments.FirstOrDefault()?.Method.ToString() ?? "Settled";
                wpReceiptUrl = WhatsAppHelper.BuildWhatsAppUrl(
                    order.CustomerPhone,
                    WhatsAppHelper.BuildBillReceiptMessage(restName, order.Bill.BillNumber, order.OrderNumber, order.Table?.TableNumber ?? "Table", order.Bill.GrandTotal, payMethod, receiptUrl)
                );
            }

            return Ok(new
            {
                success = true,
                customerPhone = order.CustomerPhone,
                customerName = order.CustomerName,
                whatsAppOrderUrl = wpOrderUrl,
                whatsAppReceiptUrl = wpReceiptUrl
            });
        }

        [HttpGet("api/order/whatsapp-link")]
        public async Task<IActionResult> GetWhatsAppLink([FromQuery] string? orderToken, [FromQuery] string type = "confirm", [FromQuery] string? phone = null)
        {
            if (string.IsNullOrWhiteSpace(orderToken)) return NotFound("Order token is required");

            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Table)
                .Include(o => o.Restaurant)
                .Include(o => o.Bill)
                    .ThenInclude(b => b!.Payments)
                .FirstOrDefaultAsync(o => o.OrderToken == orderToken);

            if (order == null) return NotFound("Order not found");

            // Strictly use the diner's saved order phone, ignoring any client-supplied phone
            string? targetPhone = order.CustomerPhone;

            var baseUrl = NetworkHelper.ResolveBaseUrl(Request, _configuration);
            var trackerUrl = $"{baseUrl}/order/track/{order.OrderToken}";
            var restName = order.Restaurant?.Name ?? "Brew & Bliss Coffee House";

            string message;
            if (type.Equals("ready", StringComparison.OrdinalIgnoreCase))
            {
                message = WhatsAppHelper.BuildFoodReadyMessage(restName, order.OrderNumber, order.Table?.TableNumber ?? "Table", trackerUrl);
            }
            else if (type.Equals("bill", StringComparison.OrdinalIgnoreCase) && order.Bill != null)
            {
                var receiptUrl = $"{baseUrl}/Billing/PrintReceipt?orderId={order.Id}&token={order.OrderToken}";
                var payMethod = order.Bill.Payments.FirstOrDefault()?.Method.ToString() ?? "Settled";
                message = WhatsAppHelper.BuildBillReceiptMessage(restName, order.Bill.BillNumber, order.OrderNumber, order.Table?.TableNumber ?? "Table", order.Bill.GrandTotal, payMethod, receiptUrl);
            }
            else
            {
                message = WhatsAppHelper.BuildOrderConfirmedMessage(restName, order.OrderNumber, order.Table?.TableNumber ?? "Table", order.TotalAmount, trackerUrl);
            }

            var wpUrl = WhatsAppHelper.BuildWhatsAppUrl(targetPhone, message, forceWeb: true);
            return Ok(new
            {
                phone = targetPhone,
                message = message,
                url = wpUrl
            });
        }

        [HttpPost("api/table/call-waiter")]
        [IgnoreAntiforgeryToken]
        [EnableRateLimiting("diner-api-policy")]
        public async Task<IActionResult> CallWaiter([FromBody] CallWaiterDto? dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.TableToken) || string.IsNullOrWhiteSpace(dto.RestaurantSlug))
            {
                return BadRequest(new { message = "Invalid request details." });
            }

            if (!string.IsNullOrWhiteSpace(dto.Notes) && dto.Notes.Trim().Length > 200)
            {
                return BadRequest(new { message = "Notes cannot exceed 200 characters." });
            }

            if (!string.IsNullOrWhiteSpace(dto.RequestType) && dto.RequestType.Trim().Length > 50)
            {
                return BadRequest(new { message = "Request type cannot exceed 50 characters." });
            }

            if (!ModelState.IsValid)
            {
                var firstError = ModelState.Values.SelectMany(v => v.Errors).FirstOrDefault()?.ErrorMessage ?? "Invalid request details.";
                return BadRequest(new { message = firstError });
            }

            var restaurant = await _context.Restaurants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Slug == dto.RestaurantSlug && r.IsActive);
            if (restaurant == null) return BadRequest(new { message = "Restaurant not found." });

            var table = await _context.DiningTables.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.RestaurantId == restaurant.Id && t.Token == dto.TableToken && t.IsActive);
            if (table == null) return BadRequest(new { message = "Table verification failed." });

            var serviceReq = new ServiceRequest
            {
                RestaurantId = restaurant.Id,
                TableId = table.Id,
                RequestType = string.IsNullOrWhiteSpace(dto.RequestType) ? "Assistance" : dto.RequestType,
                Note = dto.Notes,
                CreatedAtUtc = DateTime.UtcNow
            };

            _context.ServiceRequests.Add(serviceReq);
            await _context.SaveChangesAsync();

            // Real-time broadcast to Cashier and Kitchen dashboards
            await _hubContext.Clients.Group($"Restaurant_{restaurant.Id}")
                .SendAsync("ReceiveWaiterCall", new
                {
                    requestId = serviceReq.Id,
                    tableNumber = table.TableNumber,
                    requestType = serviceReq.RequestType,
                    notes = serviceReq.Note,
                    createdAt = _timeHelper.Format(serviceReq.CreatedAtUtc, "hh:mm tt")
                });

            _logger.LogInformation("Service request '{Type}' logged for {TableNumber} at {Restaurant}",
                serviceReq.RequestType, table.TableNumber, restaurant.Name);

            return Ok(new
            {
                success = true,
                message = $"Request sent! Staff at {restaurant.Name} has been notified."
            });
        }

        [HttpPost("api/order/feedback")]
        [IgnoreAntiforgeryToken]
        [EnableRateLimiting("diner-api-policy")]
        public async Task<IActionResult> SubmitFeedback([FromBody] FeedbackSubmissionDto? dto)
        {
            if (dto == null || !ModelState.IsValid || string.IsNullOrWhiteSpace(dto.OrderToken))
                return BadRequest(new { success = false, message = "Invalid order token or feedback data." });

            if (!string.IsNullOrWhiteSpace(dto.Comment) && dto.Comment.Trim().Length > 1000)
            {
                return BadRequest(new { success = false, message = "Comment cannot exceed 1000 characters." });
            }

            if (!string.IsNullOrWhiteSpace(dto.CustomerName) && dto.CustomerName.Trim().Length > 100)
            {
                return BadRequest(new { success = false, message = "Customer name cannot exceed 100 characters." });
            }

            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Restaurant)
                .Include(o => o.Table)
                .FirstOrDefaultAsync(o => o.OrderToken == dto.OrderToken);

            if (order == null)
                return NotFound(new { success = false, message = "Order not found." });

            // Allow feedback ONLY when the order is Served or Completed
            if (order.Status != OrderStatus.Served && order.Status != OrderStatus.Completed)
            {
                return BadRequest(new { success = false, message = "Feedback can only be submitted after your order has been served or completed." });
            }

            var existingFeedback = await _context.CustomerFeedbacks
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(f => f.OrderId == order.Id);

            if (existingFeedback != null)
            {
                return BadRequest(new { success = false, message = "Feedback has already been submitted for this order." });
            }

            if (dto.Tags != null)
            {
                if (dto.Tags.Count > 8)
                {
                    return BadRequest(new { success = false, message = "A maximum of 8 tags are allowed." });
                }
                if (dto.Tags.Any(t => t != null && t.Length > 40))
                {
                    return BadRequest(new { success = false, message = "Each tag must be 40 characters or fewer." });
                }
            }

            var tagsStr = dto.Tags != null && dto.Tags.Count > 0 
                ? string.Join(", ", dto.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())) 
                : null;

            var feedback = new CustomerFeedback
            {
                RestaurantId = order.RestaurantId,
                OrderId = order.Id,
                TableId = order.TableId,
                OverallRating = Math.Clamp(dto.OverallRating, 1, 5),
                FoodRating = Math.Clamp(dto.FoodRating, 1, 5),
                ServiceRating = Math.Clamp(dto.ServiceRating, 1, 5),
                CustomerName = string.IsNullOrWhiteSpace(dto.CustomerName) ? "Anonymous Guest" : dto.CustomerName.Trim(),
                Tags = tagsStr,
                Comment = dto.Comment?.Trim(),
                CreatedAtUtc = DateTime.UtcNow
            };

            try
            {
                _context.CustomerFeedbacks.Add(feedback);
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueIndexViolation(ex))
            {
                _logger.LogWarning(ex, "Concurrent duplicate feedback submission for Order {OrderId}", order.Id);
                return BadRequest(new { success = false, message = "Feedback has already been submitted for this order." });
            }

            _logger.LogInformation("Feedback recorded for Order {OrderNumber}: {Rating} stars by {Name}",
                order.OrderNumber, feedback.OverallRating, feedback.CustomerName);

            return Ok(new { success = true, message = "Thank you for your feedback!" });
        }

        [HttpGet("api/order/upi-payment-details/{token}")]
        public async Task<IActionResult> GetUpiPaymentDetails(string token)
        {
            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Restaurant)
                .Include(o => o.Table)
                .Include(o => o.Bill)
                .FirstOrDefaultAsync(o => o.OrderToken == token);

            if (order == null)
            {
                return NotFound(new { success = false, message = "Order not found." });
            }

            var restaurant = order.Restaurant;

            var upiRegex = new System.Text.RegularExpressions.Regex(@"^[a-zA-Z0-9.\-_]{2,256}@[a-zA-Z]{2,64}$");
            var rawUpi = restaurant?.UpiId?.Trim();
            if (string.IsNullOrWhiteSpace(rawUpi) || !upiRegex.IsMatch(rawUpi))
            {
                return BadRequest(new { success = false, message = "Restaurant has not configured a valid UPI ID." });
            }

            var upiId = rawUpi.ToLowerInvariant();
            var parts = upiId.Split('@');
            var selectedHandle = parts.Length > 1 ? parts[1] : "upi";

            // Target payee mobile number from restaurant phone digits
            var targetMobile = string.Empty;
            if (!string.IsNullOrWhiteSpace(restaurant?.Phone))
            {
                var digitsOnly = new string(restaurant.Phone.Where(char.IsDigit).ToArray());
                if (digitsOnly.Length == 12 && digitsOnly.StartsWith("91"))
                {
                    targetMobile = digitsOnly.Substring(2);
                }
                else if (digitsOnly.Length == 10)
                {
                    targetMobile = digitsOnly;
                }
            }

            var grandTotalStr = order.TotalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            var restName = restaurant?.Name ?? "Spice Symphony Restaurant";
            var payeeDisplayName = !string.IsNullOrWhiteSpace(restaurant?.PayeeName) ? restaurant.PayeeName : restName;
            var cleanPayeeName = payeeDisplayName.Replace("&", "and").Replace("#", "").Replace("+", " ").Trim();
            var safePa = Uri.EscapeDataString(upiId);
            var safePn = Uri.EscapeDataString(cleanPayeeName);
            var safeTn = Uri.EscapeDataString($"Order {order.OrderNumber} Table {order.Table?.TableNumber ?? ""}".Trim());

            var upiUri = $"upi://pay?pa={safePa}&pn={safePn}&am={grandTotalStr}&cu=INR&tn={safeTn}";
            var upiQr = _qrService.GenerateQrCodeDataUri(upiUri);

            var isPaid = order.Status == OrderStatus.Completed || (order.Bill != null && order.Bill.Status == BillStatus.Paid);

            var appUris = new Dictionary<string, string>
            {
                { "generic", upiUri },
                { "gpay", upiUri },
                { "phonepe", upiUri },
                { "paytm", upiUri }
            };

            return Ok(new UpiPaymentDetailsDto
            {
                Success = true,
                OrderNumber = order.OrderNumber,
                TableNumber = order.Table?.TableNumber ?? "Unknown",
                RestaurantName = restName,
                PhoneNumber = targetMobile,
                SubTotal = order.SubTotal,
                TaxRate = order.TaxRate,
                TaxAmount = order.TaxAmount,
                GrandTotal = order.TotalAmount,
                UpiId = upiId,
                SelectedHandle = selectedHandle,
                UpiUri = upiUri,
                UpiQrDataUri = upiQr,
                IsAlreadyPaid = isPaid,
                BillNumber = order.Bill?.BillNumber,
                PayeeName = restaurant?.PayeeName ?? restName,
                PaymentQrImageUrl = restaurant?.SafePaymentQrImageUrl,
                AppUris = appUris
            });
        }

        [HttpPost("api/restaurant/save-upi-id")]
        [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> SaveRestaurantUpiId([FromBody] SaveRestaurantUpiDto? dto)
        {
            if (dto == null || !ModelState.IsValid || (string.IsNullOrWhiteSpace(dto.UpiId) && string.IsNullOrWhiteSpace(dto.PayeeName)))
            {
                return BadRequest(new { success = false, message = "Invalid restaurant UPI data. Both UPI ID and Payee Name cannot be empty." });
            }

            // Always resolve target restaurant from the authenticated user's tenant claim, never solely from request parameters
            Guid? targetRestaurantId = null;
            if (!User.IsInRole(AppRoles.SuperAdmin))
            {
                var userClaim = User.FindFirst("RestaurantId");
                if (userClaim == null || !Guid.TryParse(userClaim.Value, out var claimTenantId))
                {
                    return Forbid();
                }
                if (!string.IsNullOrWhiteSpace(dto.RestaurantSlug))
                {
                    var slugRest = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Slug == dto.RestaurantSlug);
                    if (slugRest == null || slugRest.Id != claimTenantId)
                    {
                        return Forbid();
                    }
                }
                targetRestaurantId = claimTenantId;
            }
            else
            {
                targetRestaurantId = _tenantService.CurrentRestaurantId;
                if (!targetRestaurantId.HasValue && !string.IsNullOrWhiteSpace(dto.RestaurantSlug))
                {
                    var slugRest = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Slug == dto.RestaurantSlug);
                    targetRestaurantId = slugRest?.Id;
                }
            }

            if (!targetRestaurantId.HasValue)
            {
                return Forbid();
            }

            var restaurant = await _context.Restaurants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == targetRestaurantId.Value && r.IsActive);

            if (restaurant == null)
            {
                return NotFound(new { success = false, message = "Restaurant not found." });
            }

            if (!string.IsNullOrWhiteSpace(dto.UpiId))
            {
                var upiRegex = new System.Text.RegularExpressions.Regex(@"^[a-zA-Z0-9.\-_]{2,256}@[a-zA-Z]{2,64}$");
                if (!upiRegex.IsMatch(dto.UpiId.Trim()))
                {
                    return BadRequest(new { success = false, message = "Invalid UPI ID format. Must match regex ^[a-zA-Z0-9.\\-_]{2,256}@[a-zA-Z]{2,64}$ (e.g. restaurant@okaxis)." });
                }
                restaurant.UpiId = dto.UpiId.Trim().ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(dto.PayeeName))
            {
                restaurant.PayeeName = dto.PayeeName.Trim();
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Saved default UPI ID {UpiId} and PayeeName '{Payee}' for restaurant {Name}", 
                restaurant.UpiId, restaurant.PayeeName, restaurant.Name);

            return Ok(new
            {
                success = true,
                upiId = restaurant.UpiId,
                payeeName = restaurant.PayeeName,
                paymentQrImageUrl = restaurant.SafePaymentQrImageUrl,
                message = "Payment configuration updated successfully!"
            });
        }

        [HttpPost("api/restaurant/upload-payment-qr")]
        [Authorize(Roles = $"{AppRoles.RestaurantAdmin},{AppRoles.SuperAdmin}")]
        public async Task<IActionResult> UploadPaymentQr([FromForm] IFormFile file, [FromForm] string? restaurantSlug)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { success = false, message = "Please select a valid image file." });
            }

            if (file.Length > 5 * 1024 * 1024)
            {
                return BadRequest(new { success = false, message = "QR image file is too large (Maximum allowed size is 5MB)." });
            }

            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowedExtensions.Contains(ext))
            {
                return BadRequest(new { success = false, message = "Only JPG, PNG, and WEBP image files are allowed." });
            }

            using (var checkStream = file.OpenReadStream())
            {
                if (!NetworkHelper.IsValidImageMagicBytes(checkStream))
                {
                    return BadRequest(new { success = false, message = "Invalid image file content." });
                }
            }

            // Always resolve target restaurant from the authenticated user's tenant claim, never solely from request parameters
            Guid? targetRestaurantId = null;
            if (!User.IsInRole(AppRoles.SuperAdmin))
            {
                var userClaim = User.FindFirst("RestaurantId");
                if (userClaim == null || !Guid.TryParse(userClaim.Value, out var claimTenantId))
                {
                    return Forbid();
                }
                if (!string.IsNullOrWhiteSpace(restaurantSlug))
                {
                    var slugRest = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Slug == restaurantSlug);
                    if (slugRest == null || slugRest.Id != claimTenantId)
                    {
                        return Forbid();
                    }
                }
                targetRestaurantId = claimTenantId;
            }
            else
            {
                targetRestaurantId = _tenantService.CurrentRestaurantId;
                if (!targetRestaurantId.HasValue && !string.IsNullOrWhiteSpace(restaurantSlug))
                {
                    var slugRest = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Slug == restaurantSlug);
                    targetRestaurantId = slugRest?.Id;
                }
            }

            if (!targetRestaurantId.HasValue)
            {
                return Forbid();
            }

            var restaurant = await _context.Restaurants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == targetRestaurantId.Value && r.IsActive);
            if (restaurant == null)
            {
                return NotFound(new { success = false, message = "Restaurant not found." });
            }

            // Delete old file when replacing
            if (!string.IsNullOrWhiteSpace(restaurant.PaymentQrImageUrl))
            {
                try
                {
                    var relative = restaurant.PaymentQrImageUrl.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
                    var oldPath = Path.Combine(_env.WebRootPath, relative);
                    if (System.IO.File.Exists(oldPath))
                    {
                        System.IO.File.Delete(oldPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete old payment QR file: {Url}", restaurant.PaymentQrImageUrl);
                }
            }

            var uploadFolder = Path.Combine(_env.WebRootPath, "uploads", "payment-qrs");
            if (!Directory.Exists(uploadFolder))
            {
                Directory.CreateDirectory(uploadFolder);
            }

            var fileName = $"{Guid.NewGuid():N}{ext}";
            var filePath = Path.Combine(uploadFolder, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            restaurant.PaymentQrImageUrl = $"/uploads/payment-qrs/{fileName}";
            await _context.SaveChangesAsync();

            _logger.LogInformation("Saved real payment QR image {Url} for restaurant {Name}", restaurant.PaymentQrImageUrl, restaurant.Name);

            return Ok(new
            {
                success = true,
                imageUrl = restaurant.PaymentQrImageUrl,
                message = "Official Scanner QR image uploaded and saved successfully!"
            });
        }

        [HttpPost("api/order/confirm-online-payment")]
        [IgnoreAntiforgeryToken]
        [EnableRateLimiting("diner-api-policy")]
        public async Task<IActionResult> ConfirmOnlinePayment([FromBody] ConfirmOnlinePaymentDto? dto)
        {
            if (dto == null || !ModelState.IsValid || string.IsNullOrWhiteSpace(dto.OrderToken))
                return BadRequest(new { success = false, message = "Invalid payment request or order token." });

            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Restaurant)
                .Include(o => o.Table)
                .Include(o => o.Bill)
                .FirstOrDefaultAsync(o => o.OrderToken == dto.OrderToken);

            if (order == null)
                return NotFound(new { success = false, message = "Order not found." });

            if (order.Status == OrderStatus.Completed && order.Bill != null && order.Bill.Status == BillStatus.Paid)
            {
                return Ok(new
                {
                    success = true,
                    alreadyPaid = true,
                    billNumber = order.Bill.BillNumber,
                    grandTotal = order.Bill.GrandTotal,
                    message = "This bill has already been settled and paid."
                });
            }

            try
            {
                // Security rule: Diners cannot self-settle bills or unlock tax receipts.
                // Cashier at /billing MUST verify bank credit and approve the settlement.
                var pendingRequest = await _context.ServiceRequests
                    .IgnoreQueryFilters()
                    .Where(s => s.RestaurantId == order.RestaurantId
                             && (s.OrderId == order.Id || (s.OrderId == null && s.TableId == order.TableId))
                             && s.RequestType == "UPI Payment"
                             && !s.IsHandled)
                    .OrderByDescending(s => s.CreatedAtUtc)
                    .FirstOrDefaultAsync();

                if (pendingRequest == null)
                {
                    pendingRequest = new ServiceRequest
                    {
                        RestaurantId = order.RestaurantId,
                        TableId = order.TableId,
                        OrderId = order.Id,
                        RequestType = "UPI Payment",
                        Note = $"Order {order.OrderNumber} (Table {order.Table?.TableNumber ?? "Table"}): reported UPI payment of ₹{order.TotalAmount:F2}. Verify bank SMS/Soundbox before approving.",
                        CreatedAtUtc = DateTime.UtcNow
                    };
                    _context.ServiceRequests.Add(pendingRequest);
                }
                else
                {
                    pendingRequest.OrderId = order.Id;
                    pendingRequest.Note = $"Order {order.OrderNumber} (Table {order.Table?.TableNumber ?? "Table"}): reminder of ₹{order.TotalAmount:F2} via UPI awaiting approval.";
                    pendingRequest.CreatedAtUtc = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();

                // Real-time broadcast to Cashier dashboard
                await _hubContext.Clients.Group($"Restaurant_{order.RestaurantId}")
                    .SendAsync("ReceiveWaiterCall", new
                    {
                        requestId = pendingRequest.Id,
                        tableNumber = order.Table?.TableNumber ?? "Table",
                        requestType = "UPI Payment",
                        notes = pendingRequest.Note,
                        createdAt = _timeHelper.Format(pendingRequest.CreatedAtUtc, "hh:mm tt"),
                        orderId = order.Id,
                        totalAmount = order.TotalAmount
                    });

                _logger.LogInformation("Customer at Table {Table} (Order {OrderNumber}) reported UPI payment ₹{Amount}. Awaiting cashier approval.",
                    order.Table?.TableNumber, order.OrderNumber, order.TotalAmount);

                return Ok(new
                {
                    success = true,
                    awaitingCashierApproval = true,
                    orderNumber = order.OrderNumber,
                    tableNumber = order.Table?.TableNumber ?? "Table",
                    grandTotal = order.TotalAmount,
                    message = "Notification sent to Cashier! Please wait for Cashier to verify payment in bank and approve your bill."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to notify cashier of online payment for order {OrderToken}", dto.OrderToken);
                return StatusCode(500, new { success = false, message = "Could not notify cashier. Please ask staff for assistance." });
            }
        }
    }

    public class CallWaiterDto
    {
        [System.ComponentModel.DataAnnotations.Required]
        [System.ComponentModel.DataAnnotations.MaxLength(100)]
        public string RestaurantSlug { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.Required]
        [System.ComponentModel.DataAnnotations.MaxLength(64)]
        public string TableToken { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.MaxLength(50)]
        public string RequestType { get; set; } = "Assistance";

        [System.ComponentModel.DataAnnotations.MaxLength(200)]
        public string? Notes { get; set; }
    }

    public class SaveRestaurantUpiDto
    {
        [System.ComponentModel.DataAnnotations.Required]
        [System.ComponentModel.DataAnnotations.MaxLength(100)]
        public string RestaurantSlug { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.MaxLength(100)]
        public string? UpiId { get; set; }

        [System.ComponentModel.DataAnnotations.MaxLength(100)]
        public string? PayeeName { get; set; }
    }
}
