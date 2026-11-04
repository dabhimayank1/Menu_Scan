using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MenuScan.Data;
using MenuScan.Models;
using MenuScan.Models.Entities;
using MenuScan.Services;

namespace MenuScan.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly ApplicationDbContext _context;
    private readonly IQRService _qrService;
    private readonly ITenantService _tenantService;
    private readonly IConfiguration _configuration;

    public HomeController(ILogger<HomeController> logger, ApplicationDbContext context, IQRService qrService, ITenantService tenantService, IConfiguration configuration)
    {
        _logger = logger;
        _context = context;
        _qrService = qrService;
        _tenantService = tenantService;
        _configuration = configuration;
    }

    public async Task<IActionResult> Index()
    {
        var tenantId = _tenantService.CurrentRestaurantId;
        Restaurant? restaurant = null;
        if (tenantId.HasValue)
        {
            restaurant = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == tenantId.Value);
        }
        if (restaurant == null)
        {
            restaurant = await _context.Restaurants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Slug == "spice-symphony" && r.IsActive)
                ?? await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.IsActive);
        }

        string tableNumber = "Table 1";
        string tableToken = "TAB-01";
        string slug = restaurant?.Slug ?? "spice-symphony";
        string restName = restaurant?.Name ?? "Brew & Bliss Coffee House";

        if (restaurant != null)
        {
            var table = await _context.DiningTables.IgnoreQueryFilters()
                .Where(t => t.RestaurantId == restaurant.Id)
                .OrderBy(t => t.TableNumber)
                .FirstOrDefaultAsync();

            if (table != null)
            {
                tableNumber = table.TableNumber;
                tableToken = table.Token;
            }
        }

        var baseUrl = NetworkHelper.ResolveBaseUrl(Request, _configuration);
        var customerUrl = $"{baseUrl}/r/{slug}/t/{tableToken}";
        var qrDataUri = _qrService.GenerateQrCodeDataUri(customerUrl);

        ViewBag.CustomerUrl = customerUrl;
        ViewBag.QrCodeDataUri = qrDataUri;
        ViewBag.TableNumber = tableNumber;
        ViewBag.RestaurantName = restName;
        ViewBag.Host = baseUrl;

        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
