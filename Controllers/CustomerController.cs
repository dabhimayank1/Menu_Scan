using MenuScan.Data;
using MenuScan.Hubs;
using MenuScan.Models.Entities;
using MenuScan.Models.ViewModels;
using MenuScan.Services;
using Microsoft.AspNetCore.Mvc;
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

        public CustomerController(
            ApplicationDbContext context,
            IHubContext<OrderHub> hubContext,
            IQRService qrService,
            ILogger<CustomerController> logger)
        {
            _context = context;
            _hubContext = hubContext;
            _qrService = qrService;
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

            var hasFeedback = await _context.CustomerFeedbacks
                .IgnoreQueryFilters()
                .AnyAsync(f => f.OrderId == order.Id);

            var viewModel = new OrderTrackingViewModel
            {
                Order = order,
                Restaurant = restaurant!,
                Table = order.Table!,
                HasSubmittedFeedback = hasFeedback
            };

            return View(viewModel);
        }

        [HttpPost("api/table/call-waiter")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> CallWaiter([FromBody] CallWaiterDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.TableToken) || string.IsNullOrWhiteSpace(dto.RestaurantSlug))
            {
                return BadRequest(new { message = "Invalid request details." });
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
                    createdAt = serviceReq.CreatedAtUtc.ToLocalTime().ToString("hh:mm tt")
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
        public async Task<IActionResult> SubmitFeedback([FromBody] FeedbackSubmissionDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.OrderToken))
                return BadRequest(new { success = false, message = "Invalid order token." });

            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Restaurant)
                .Include(o => o.Table)
                .FirstOrDefaultAsync(o => o.OrderToken == dto.OrderToken);

            if (order == null)
                return NotFound(new { success = false, message = "Order not found." });

            var existingFeedback = await _context.CustomerFeedbacks
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(f => f.OrderId == order.Id);

            if (existingFeedback != null)
            {
                return BadRequest(new { success = false, message = "Feedback has already been submitted for this order." });
            }

            var tagsStr = dto.Tags != null && dto.Tags.Count > 0 ? string.Join(", ", dto.Tags) : null;

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

            _context.CustomerFeedbacks.Add(feedback);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Feedback recorded for Order {OrderNumber}: {Rating} stars by {Name}",
                order.OrderNumber, feedback.OverallRating, feedback.CustomerName);

            return Ok(new { success = true, message = "Thank you for your feedback!" });
        }

        [HttpGet("api/order/upi-payment-details/{token}")]
        public async Task<IActionResult> GetUpiPaymentDetails(string token, [FromQuery] string? handle = "upi")
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

            // Target payee mobile number: default 8799020418 or restaurant phone digits
            var targetMobile = "8799020418";
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

            // Supported UPI handles
            var cleanHandle = string.IsNullOrWhiteSpace(handle) ? "upi" : handle.Trim().TrimStart('@').ToLowerInvariant();
            var validHandles = new HashSet<string> { "upi", "paytm", "ybl", "okaxis", "okhdfcbank", "ibl" };
            if (!validHandles.Contains(cleanHandle))
            {
                cleanHandle = "upi";
            }

            var upiId = $"{targetMobile}@{cleanHandle}";
            var grandTotalStr = order.TotalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            var restName = restaurant?.Name ?? "Spice Symphony Restaurant";
            var payeeName = Uri.EscapeDataString(restName);
            var note = Uri.EscapeDataString($"Order #{order.OrderNumber} Table {order.Table?.TableNumber ?? ""} at {restName}");

            var upiUri = $"upi://pay?pa={upiId}&pn={payeeName}&am={grandTotalStr}&tr={order.OrderNumber}&tn={note}&cu=INR";
            var upiQr = _qrService.GenerateQrCodeDataUri(upiUri);

            var isPaid = order.Status == OrderStatus.Completed || (order.Bill != null && order.Bill.Status == BillStatus.Paid);

            var appUris = new Dictionary<string, string>
            {
                { "generic", upiUri },
                { "gpay", $"upi://pay?pa={targetMobile}@okaxis&pn={payeeName}&am={grandTotalStr}&tr={order.OrderNumber}&tn={note}&cu=INR" },
                { "phonepe", $"upi://pay?pa={targetMobile}@ybl&pn={payeeName}&am={grandTotalStr}&tr={order.OrderNumber}&tn={note}&cu=INR" },
                { "paytm", $"upi://pay?pa={targetMobile}@paytm&pn={payeeName}&am={grandTotalStr}&tr={order.OrderNumber}&tn={note}&cu=INR" }
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
                SelectedHandle = cleanHandle,
                UpiUri = upiUri,
                UpiQrDataUri = upiQr,
                IsAlreadyPaid = isPaid,
                BillNumber = order.Bill?.BillNumber,
                AppUris = appUris
            });
        }

        [HttpPost("api/order/confirm-online-payment")]
        public async Task<IActionResult> ConfirmOnlinePayment([FromBody] ConfirmOnlinePaymentDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.OrderToken))
                return BadRequest(new { success = false, message = "Invalid order token." });

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
                    message = "This bill has already been settled."
                });
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

                var paymentRef = string.IsNullOrWhiteSpace(dto.PaymentReference)
                    ? $"UPI-{DateTime.UtcNow:yyMMddHHmmss}-{Random.Shared.Next(100, 999)}"
                    : dto.PaymentReference.Trim();

                var payment = new Payment
                {
                    BillId = bill.Id,
                    Amount = bill.GrandTotal,
                    Method = PaymentMethod.UPI,
                    Reference = paymentRef,
                    RecordedBy = "Customer (UPI Self-Checkout)",
                    PaidAtUtc = DateTime.UtcNow
                };
                _context.Payments.Add(payment);

                var oldStatus = order.Status;
                order.Status = OrderStatus.Completed;

                _context.OrderStatusHistories.Add(new OrderStatusHistory
                {
                    OrderId = order.Id,
                    OldStatus = oldStatus,
                    NewStatus = OrderStatus.Completed,
                    ChangedBy = "Customer Online",
                    ChangedAtUtc = DateTime.UtcNow,
                    Notes = $"Self-Checkout via UPI ({bill.BillNumber}) Ref: {paymentRef}"
                });

                var pendingBillRequests = await _context.ServiceRequests
                    .IgnoreQueryFilters()
                    .Where(s => s.TableId == order.TableId && !s.IsHandled && s.RequestType == "Bill")
                    .ToListAsync();

                foreach (var req in pendingBillRequests)
                {
                    req.IsHandled = true;
                    req.HandledAtUtc = DateTime.UtcNow;
                    req.HandledBy = "Customer Self-Checkout";
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                await _hubContext.Clients.Group($"Restaurant_{order.RestaurantId}")
                    .SendAsync("ReceiveOrderStatusUpdate", order.OrderNumber, "Completed", order.Table?.TableNumber ?? "Unknown");

                await _hubContext.Clients.Group($"Order_{order.OrderToken}")
                    .SendAsync("ReceiveOrderStatusUpdate", order.OrderNumber, "Completed", order.Table?.TableNumber ?? "Unknown");

                _logger.LogInformation("Order {OrderNumber} paid online by customer via UPI. Bill {BillNumber}",
                    order.OrderNumber, bill.BillNumber);

                return Ok(new
                {
                    success = true,
                    billNumber = bill.BillNumber,
                    grandTotal = bill.GrandTotal,
                    message = "Payment confirmed successfully!"
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to confirm online payment for order {OrderToken}", dto.OrderToken);
                return StatusCode(500, new { success = false, message = "Payment processing failed. Please ask staff for assistance." });
            }
        }
    }

    public class CallWaiterDto
    {
        public string RestaurantSlug { get; set; } = string.Empty;
        public string TableToken { get; set; } = string.Empty;
        public string RequestType { get; set; } = "Assistance";
        public string? Notes { get; set; }
    }
}
