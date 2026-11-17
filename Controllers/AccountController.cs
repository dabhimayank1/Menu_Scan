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
        private readonly IWebHostEnvironment _env;

        public AccountController(
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            ApplicationDbContext context,
            ITenantService tenantService,
            IWebHostEnvironment env)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _context = context;
            _tenantService = tenantService;
            _env = env;
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

                // In non-Development environments, force password change if using known weak/default password
                if (!_env.IsDevelopment() && SecurityRules.IsBlocklistedPassword(model.Password))
                {
                    TempData["Warning"] = "You are currently using a default or insecure password. For security compliance, you must set a new password before continuing.";
                    return RedirectToAction(nameof(ChangePassword));
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

        [HttpGet]
        [Authorize]
        public IActionResult ChangePassword()
        {
            return View(new ChangePasswordViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            if (string.Equals(model.CurrentPassword, model.NewPassword))
            {
                ModelState.AddModelError("NewPassword", "New password cannot be the same as your current password.");
                return View(model);
            }

            if (!SecurityRules.ValidatePassword(model.NewPassword, out var passwordError))
            {
                ModelState.AddModelError("NewPassword", passwordError!);
                return View(model);
            }

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return RedirectToAction(nameof(Login));
            }

            var result = await _userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }
                return View(model);
            }

            // Refresh security stamp to invalidate cookies on other sessions/devices
            await _userManager.UpdateSecurityStampAsync(user);
            await _signInManager.RefreshSignInAsync(user);

            TempData["Success"] = "Your password has been changed successfully.";
            return RedirectBasedOnRole();
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
