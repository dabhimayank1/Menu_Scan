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
        private readonly IWebHostEnvironment _env;

        public CustomerController(
            ApplicationDbContext context,
            IHubContext<OrderHub> hubContext,
            IQRService qrService,
            ILogger<CustomerController> logger,
            IWebHostEnvironment env)
        {
            _context = context;
            _hubContext = hubContext;
            _qrService = qrService;
            _logger = logger;
            _env = env;
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

            var activeOrders = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.OrderItems)
                .Include(o => o.Bill)
                .Where(o => o.RestaurantId == restaurant.Id && o.TableId == table.Id && o.Status != OrderStatus.Cancelled && (o.Status != OrderStatus.Completed || o.CreatedAtUtc >= DateTime.UtcNow.AddMinutes(-60)))
                .OrderByDescending(o => o.CreatedAtUtc)
                .ToListAsync();

            var viewModel = new CustomerMenuViewModel
            {
                Restaurant = restaurant,
                Table = table,
                Categories = categories,
                ActiveOrders = activeOrders
            };

            return View(viewModel);
        }

        [HttpGet("api/table/active-orders")]
        public async Task<IActionResult> GetTableActiveOrders([FromQuery] string tableToken, [FromQuery] string restaurantSlug)
        {
            var restaurant = await _context.Restaurants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Slug == restaurantSlug && r.IsActive);
            if (restaurant == null) return NotFound();

            var table = await _context.DiningTables.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.RestaurantId == restaurant.Id && t.Token == tableToken && t.IsActive);
            if (table == null) return NotFound();

            var orders = await _context.Orders.IgnoreQueryFilters()
                .Include(o => o.OrderItems)
                .Include(o => o.Bill)
                .Where(o => o.RestaurantId == restaurant.Id && o.TableId == table.Id && o.Status != OrderStatus.Cancelled && (o.Status != OrderStatus.Completed || o.CreatedAtUtc >= DateTime.UtcNow.AddMinutes(-60)))
                .OrderByDescending(o => o.CreatedAtUtc)
                .Take(5)
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
                    createdAt = o.CreatedAtUtc.ToLocalTime().ToString("hh:mm tt"),
                    isPaid = o.Status == OrderStatus.Completed || (o.Bill != null && o.Bill.Status == BillStatus.Paid),
                    billNumber = o.Bill != null ? o.Bill.BillNumber : null
                })
                .ToListAsync();

            return Ok(orders);
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
                    CustomerName = dto.CustomerName,
                    CustomerPhone = dto.CustomerPhone,
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
                    notes = order.CustomerNotes,
                    customerName = order.CustomerName,
                    customerPhone = order.CustomerPhone
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
                .Include(o => o.Bill)
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

            var host = NetworkHelper.ResolveReachableHost(Request);
            var trackerUrl = $"http://{host}/order/track/{order.OrderToken}";
            var restName = restaurant?.Name ?? "Brew & Bliss Coffee House";

            ViewBag.TrackerUrl = trackerUrl;
            ViewBag.Host = host;
            ViewBag.WhatsAppOrderConfirmedUrl = WhatsAppHelper.BuildWhatsAppUrl(
                order.CustomerPhone,
                WhatsAppHelper.BuildOrderConfirmedMessage(restName, order.OrderNumber, order.Table?.TableNumber ?? "Table", order.TotalAmount, trackerUrl)
            );

            if (order.Bill != null)
            {
                var receiptUrl = $"http://{host}/Billing/PrintReceipt?orderId={order.Id}";
                ViewBag.WhatsAppReceiptUrl = WhatsAppHelper.BuildWhatsAppUrl(
                    order.CustomerPhone,
                    WhatsAppHelper.BuildBillReceiptMessage(restName, order.Bill.BillNumber, order.OrderNumber, order.Table?.TableNumber ?? "Table", order.Bill.GrandTotal, "UPI", receiptUrl)
                );
            }

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
        public async Task<IActionResult> SaveCustomerContact([FromBody] UpdateCustomerContactDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.OrderToken))
                return BadRequest(new { message = "Order token is required." });

            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Table)
                .Include(o => o.Restaurant)
                .Include(o => o.Bill)
                .FirstOrDefaultAsync(o => o.OrderToken == dto.OrderToken);

            if (order == null) return NotFound(new { message = "Order not found." });

            if (!string.IsNullOrWhiteSpace(dto.CustomerPhone))
            {
                order.CustomerPhone = dto.CustomerPhone.Trim();
            }
            if (!string.IsNullOrWhiteSpace(dto.CustomerName))
            {
                order.CustomerName = dto.CustomerName.Trim();
            }

            await _context.SaveChangesAsync();

            var host = NetworkHelper.ResolveReachableHost(Request);
            var trackerUrl = $"http://{host}/order/track/{order.OrderToken}";
            var restName = order.Restaurant?.Name ?? "Brew & Bliss Coffee House";
            var wpOrderUrl = WhatsAppHelper.BuildWhatsAppUrl(
                order.CustomerPhone,
                WhatsAppHelper.BuildOrderConfirmedMessage(restName, order.OrderNumber, order.Table?.TableNumber ?? "Table", order.TotalAmount, trackerUrl)
            );

            string? wpReceiptUrl = null;
            if (order.Bill != null)
            {
                var receiptUrl = $"http://{host}/Billing/PrintReceipt?orderId={order.Id}";
                wpReceiptUrl = WhatsAppHelper.BuildWhatsAppUrl(
                    order.CustomerPhone,
                    WhatsAppHelper.BuildBillReceiptMessage(restName, order.Bill.BillNumber, order.OrderNumber, order.Table?.TableNumber ?? "Table", order.Bill.GrandTotal, "UPI", receiptUrl)
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
        public async Task<IActionResult> GetWhatsAppLink([FromQuery] string orderId, [FromQuery] string type = "confirm", [FromQuery] string? phone = null)
        {
            if (!Guid.TryParse(orderId, out var id)) return BadRequest("Invalid order ID");

            var order = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Table)
                .Include(o => o.Restaurant)
                .Include(o => o.Bill)
                .FirstOrDefaultAsync(o => o.Id == id);

            if (order == null) return NotFound();

            var targetPhone = !string.IsNullOrWhiteSpace(phone) ? phone : order.CustomerPhone;
            var host = NetworkHelper.ResolveReachableHost(Request);
            var trackerUrl = $"http://{host}/order/track/{order.OrderToken}";
            var restName = order.Restaurant?.Name ?? "Brew & Bliss Coffee House";

            string message;
            if (type.Equals("ready", StringComparison.OrdinalIgnoreCase))
            {
                message = WhatsAppHelper.BuildFoodReadyMessage(restName, order.OrderNumber, order.Table?.TableNumber ?? "Table", trackerUrl);
            }
            else if (type.Equals("bill", StringComparison.OrdinalIgnoreCase) && order.Bill != null)
            {
                var receiptUrl = $"http://{host}/Billing/PrintReceipt?orderId={order.Id}";
                message = WhatsAppHelper.BuildBillReceiptMessage(restName, order.Bill.BillNumber, order.OrderNumber, order.Table?.TableNumber ?? "Table", order.Bill.GrandTotal, "UPI", receiptUrl);
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
        public async Task<IActionResult> GetUpiPaymentDetails(string token, [FromQuery] string? handle = null, [FromQuery] string? customUpi = null)
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

            // Determine final Payee UPI ID
            string upiId;
            string selectedHandle;

            if (!string.IsNullOrWhiteSpace(customUpi) && customUpi.Contains('@'))
            {
                upiId = customUpi.Trim().ToLowerInvariant();
                var parts = upiId.Split('@');
                selectedHandle = parts.Length > 1 ? parts[1] : "custom";
            }
            else if (!string.IsNullOrWhiteSpace(restaurant?.UpiId) && restaurant.UpiId.Contains('@'))
            {
                var savedUpi = restaurant.UpiId.Trim().ToLowerInvariant();
                var savedParts = savedUpi.Split('@');
                var savedHandle = savedParts.Length > 1 ? savedParts[1] : "okhdfcbank";

                if (!string.IsNullOrWhiteSpace(handle) && handle != "default" && handle != savedHandle)
                {
                    var cleanHandle = handle.Trim().TrimStart('@').ToLowerInvariant();
                    selectedHandle = cleanHandle;
                    upiId = $"{targetMobile}@{cleanHandle}";
                }
                else
                {
                    upiId = savedUpi;
                    selectedHandle = savedHandle;
                }
            }
            else
            {
                var cleanHandle = string.IsNullOrWhiteSpace(handle) ? "okhdfcbank" : handle.Trim().TrimStart('@').ToLowerInvariant();
                selectedHandle = cleanHandle;
                upiId = $"{targetMobile}@{cleanHandle}";
            }

            var grandTotalStr = order.TotalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            var restName = restaurant?.Name ?? "Spice Symphony Restaurant";
            var payeeDisplayName = !string.IsNullOrWhiteSpace(restaurant?.PayeeName) ? restaurant.PayeeName : restName;
            var cleanPayeeName = payeeDisplayName.Replace("&", "and").Replace("#", "").Replace("+", " ").Trim();
            var safePayeeName = Uri.EscapeDataString(cleanPayeeName);
            var safeNote = Uri.EscapeDataString($"Order {order.OrderNumber} Table {order.Table?.TableNumber ?? ""}".Trim());

            var aidParam = upiId.Contains("dabhimayank") ? "&aid=uGICAgMCRn5uuCQ" : "";
            var upiUri = $"upi://pay?pa={upiId}&pn={safePayeeName}&am={grandTotalStr}&cu=INR{aidParam}&tn={safeNote}";
            var upiQr = _qrService.GenerateQrCodeDataUri(upiUri);

            var isPaid = order.Status == OrderStatus.Completed || (order.Bill != null && order.Bill.Status == BillStatus.Paid);

            var appUris = new Dictionary<string, string>
            {
                { "generic", upiUri },
                { "gpay", $"upi://pay?pa={upiId}&pn={safePayeeName}&am={grandTotalStr}&cu=INR{aidParam}&tn={safeNote}" },
                { "phonepe", $"upi://pay?pa={(upiId.EndsWith("@ybl") || upiId.EndsWith("@ibl") ? upiId : $"{targetMobile}@ybl")}&pn={safePayeeName}&am={grandTotalStr}&cu=INR&tn={safeNote}" },
                { "paytm", $"upi://pay?pa={(upiId.EndsWith("@paytm") ? upiId : $"{targetMobile}@paytm")}&pn={safePayeeName}&am={grandTotalStr}&cu=INR&tn={safeNote}" }
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
                PaymentQrImageUrl = restaurant?.PaymentQrImageUrl,
                AppUris = appUris
            });
        }

        [HttpPost("api/restaurant/save-upi-id")]
        public async Task<IActionResult> SaveRestaurantUpiId([FromBody] SaveRestaurantUpiDto dto)
        {
            var restaurant = await _context.Restaurants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Slug == dto.RestaurantSlug && r.IsActive);

            if (restaurant == null)
            {
                restaurant = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.IsActive);
            }

            if (restaurant == null)
            {
                return NotFound(new { success = false, message = "Restaurant not found." });
            }

            if (!string.IsNullOrWhiteSpace(dto.UpiId))
            {
                if (!dto.UpiId.Contains('@'))
                {
                    return BadRequest(new { success = false, message = "Invalid UPI ID format. Must contain '@' (e.g. 8799020418@okaxis)." });
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
                paymentQrImageUrl = restaurant.PaymentQrImageUrl,
                message = "Payment configuration updated successfully!"
            });
        }

        [HttpPost("api/restaurant/upload-payment-qr")]
        public async Task<IActionResult> UploadPaymentQr([FromForm] IFormFile file, [FromForm] string? restaurantSlug)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { success = false, message = "Please select a valid image file." });
            }

            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowedExtensions.Contains(ext))
            {
                return BadRequest(new { success = false, message = "Only JPG, PNG, and WEBP image files are allowed." });
            }

            var restaurant = await _context.Restaurants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Slug == restaurantSlug && r.IsActive);
            if (restaurant == null)
            {
                restaurant = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.IsActive);
            }
            if (restaurant == null)
            {
                return NotFound(new { success = false, message = "Restaurant not found." });
            }

            var uploadFolder = Path.Combine(_env.WebRootPath, "uploads", "payment-qrs");
            if (!Directory.Exists(uploadFolder))
            {
                Directory.CreateDirectory(uploadFolder);
            }

            var fileName = $"{restaurant.Slug}-qr-{DateTime.UtcNow.Ticks}{ext}";
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

                await _hubContext.Clients.Group($"Restaurant_{order.RestaurantId}")
                    .SendAsync("ReceivePaymentSettled", new
                    {
                        orderNumber = order.OrderNumber,
                        tableNumber = order.Table?.TableNumber ?? "Table",
                        amount = bill.GrandTotal,
                        method = "UPI",
                        billNumber = bill.BillNumber
                    });

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

    public class SaveRestaurantUpiDto
    {
        public string RestaurantSlug { get; set; } = string.Empty;
        public string? UpiId { get; set; }
        public string? PayeeName { get; set; }
    }
}
