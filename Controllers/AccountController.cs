using System.Security.Claims;
using MenuScan.Models.Entities;
using MenuScan.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace MenuScan.Controllers
{
    public class AccountController : Controller
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;

        public AccountController(
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager)
        {
            _signInManager = signInManager;
            _userManager = userManager;
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

            var result = await _signInManager.PasswordSignInAsync(user.UserName!, model.Password, model.RememberMe, lockoutOnFailure: false);
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
