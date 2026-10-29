using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MenuScan.Data;
using MenuScan.Models;
using MenuScan.Services;

namespace MenuScan.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly ApplicationDbContext _context;
    private readonly IQRService _qrService;

    public HomeController(ILogger<HomeController> logger, ApplicationDbContext context, IQRService qrService)
    {
        _logger = logger;
        _context = context;
        _qrService = qrService;
    }

    public async Task<IActionResult> Index()
    {
        var restaurant = await _context.Restaurants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Slug == "spice-symphony")
            ?? await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync();

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

        var host = NetworkHelper.ResolveReachableHost(Request);
        var customerUrl = $"http://{host}/r/{slug}/t/{tableToken}";
        var qrDataUri = _qrService.GenerateQrCodeDataUri(customerUrl);

        ViewBag.CustomerUrl = customerUrl;
        ViewBag.QrCodeDataUri = qrDataUri;
        ViewBag.TableNumber = tableNumber;
        ViewBag.RestaurantName = restName;
        ViewBag.Host = host;

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
