using System.Security.Claims;
using MenuScan.Data;
using MenuScan.Models.Entities;
using MenuScan.Models.ViewModels;
using MenuScan.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace MenuScan.Controllers
{
    public class AccountController : Controller
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _context;
        private readonly ITenantService _tenantService;

        public AccountController(
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            ApplicationDbContext context,
            ITenantService tenantService)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _context = context;
            _tenantService = tenantService;
        }

        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectBasedOnRole();
            }

            ViewData["ReturnUrl"] = returnUrl;
            return View(new LoginViewModel { ReturnUrl = returnUrl });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("login-policy")]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null || !user.IsActive)
            {
                ModelState.AddModelError(string.Empty, "Invalid login credentials or account inactive.");
                return View(model);
            }

            // In Login POST also reject users whose Restaurant.IsActive == false
            if (user.RestaurantId.HasValue)
            {
                var userRestaurant = await _context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == user.RestaurantId.Value);
                if (userRestaurant == null || !userRestaurant.IsActive)
                {
                    ModelState.AddModelError(string.Empty, "Restaurant account is deactivated. Access denied.");
                    return View(model);
                }
            }

            var result = await _signInManager.PasswordSignInAsync(user.UserName!, model.Password, model.RememberMe, lockoutOnFailure: true);
            if (result.Succeeded)
            {
                // Ensure RestaurantId claim exists in cookie
                if (user.RestaurantId.HasValue)
                {
                    var existingClaims = await _userManager.GetClaimsAsync(user);
                    if (!existingClaims.Any(c => c.Type == "RestaurantId"))
                    {
                        await _userManager.AddClaimAsync(user, new Claim("RestaurantId", user.RestaurantId.Value.ToString()));
                        await _signInManager.RefreshSignInAsync(user);
                    }
                }

                if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
                {
                    return Redirect(model.ReturnUrl);
                }

                return RedirectBasedOnRole();
            }

            if (result.IsLockedOut)
            {
                ModelState.AddModelError(string.Empty, "Account locked due to multiple failed login attempts. Please try again after 10 minutes.");
                return View(model);
            }

            ModelState.AddModelError(string.Empty, "Invalid login credentials.");
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction("Login", "Account");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = AppRoles.SuperAdmin)]
        public async Task<IActionResult> SwitchRestaurant(Guid id, string? returnUrl = null)
        {
            var exists = await _context.Restaurants.IgnoreQueryFilters().AnyAsync(r => r.Id == id && r.IsActive);
            if (exists)
            {
                _tenantService.SetCurrentRestaurant(id);
            }

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction("Index", "Admin");
        }

        [HttpGet]
        public IActionResult AccessDenied()
        {
            return View();
        }

        private IActionResult RedirectBasedOnRole()
        {
            if (User.IsInRole(AppRoles.KitchenStaff))
            {
                return RedirectToAction("Index", "Kitchen");
            }
            if (User.IsInRole(AppRoles.Cashier))
            {
                return RedirectToAction("Index", "Billing");
            }
            return RedirectToAction("Index", "Admin");
        }
    }
}
