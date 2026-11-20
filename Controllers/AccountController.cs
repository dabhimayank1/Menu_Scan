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
        public async Task<IActionResult> ChangePassword()
        {
            var user = await _userManager.GetUserAsync(User);
            return View(new ChangePasswordViewModel
            {
                CurrentEmail = user?.Email
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return RedirectToAction(nameof(Login));
            }

            model.CurrentEmail = user.Email;

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var trimmedNewEmail = model.NewEmail?.Trim();
            var trimmedNewPassword = model.NewPassword?.Trim();

            bool isChangingEmail = !string.IsNullOrWhiteSpace(trimmedNewEmail) &&
                                  !string.Equals(trimmedNewEmail, user.Email, StringComparison.OrdinalIgnoreCase);
            bool isChangingPassword = !string.IsNullOrWhiteSpace(trimmedNewPassword);

            if (!isChangingEmail && !isChangingPassword)
            {
                ModelState.AddModelError(string.Empty, "Please enter a new email address, a new password, or both to update.");
                return View(model);
            }

            // Always verify current password first
            var passwordCheck = await _userManager.CheckPasswordAsync(user, model.CurrentPassword);
            if (!passwordCheck)
            {
                ModelState.AddModelError("CurrentPassword", "Incorrect current password. Please try again.");
                return View(model);
            }

            // Validate password if changing
            if (isChangingPassword)
            {
                if (string.Equals(model.CurrentPassword, trimmedNewPassword))
                {
                    ModelState.AddModelError("NewPassword", "New password cannot be the same as your current password.");
                    return View(model);
                }

                if (!SecurityRules.ValidatePassword(trimmedNewPassword, out var passwordError))
                {
                    ModelState.AddModelError("NewPassword", passwordError!);
                    return View(model);
                }

                if (string.IsNullOrWhiteSpace(model.ConfirmPassword) || !string.Equals(trimmedNewPassword, model.ConfirmPassword.Trim()))
                {
                    ModelState.AddModelError("ConfirmPassword", "New password and confirmation password do not match.");
                    return View(model);
                }
            }

            // Validate email uniqueness if changing email
            if (isChangingEmail)
            {
                var existingUser = await _userManager.FindByEmailAsync(trimmedNewEmail!) ?? await _userManager.FindByNameAsync(trimmedNewEmail!);
                if (existingUser != null && existingUser.Id != user.Id)
                {
                    ModelState.AddModelError("NewEmail", "This email address is already in use by another account.");
                    return View(model);
                }
            }

            // Execute Password change
            if (isChangingPassword)
            {
                var passResult = await _userManager.ChangePasswordAsync(user, model.CurrentPassword, trimmedNewPassword!);
                if (!passResult.Succeeded)
                {
                    foreach (var error in passResult.Errors)
                    {
                        ModelState.AddModelError(string.Empty, error.Description);
                    }
                    return View(model);
                }
            }

            // Execute Email change
            if (isChangingEmail)
            {
                var emailToken = await _userManager.GenerateChangeEmailTokenAsync(user, trimmedNewEmail!);
                var emailResult = await _userManager.ChangeEmailAsync(user, trimmedNewEmail!, emailToken);
                if (!emailResult.Succeeded)
                {
                    foreach (var error in emailResult.Errors)
                    {
                        ModelState.AddModelError("NewEmail", error.Description);
                    }
                    return View(model);
                }

                // Also update UserName to match new email so login with new email works seamlessly
                var userNameResult = await _userManager.SetUserNameAsync(user, trimmedNewEmail!);
                if (!userNameResult.Succeeded)
                {
                    foreach (var error in userNameResult.Errors)
                    {
                        ModelState.AddModelError("NewEmail", error.Description);
                    }
                    return View(model);
                }
            }

            // Refresh security stamp and auth cookie session
            await _userManager.UpdateSecurityStampAsync(user);
            await _signInManager.RefreshSignInAsync(user);

            if (isChangingEmail && isChangingPassword)
            {
                TempData["Success"] = $"Your email has been changed to '{trimmedNewEmail}' and your password has been updated successfully.";
            }
            else if (isChangingEmail)
            {
                TempData["Success"] = $"Your email has been changed to '{trimmedNewEmail}' successfully. Use this new email for future logins.";
            }
            else
            {
                TempData["Success"] = "Your password has been changed successfully.";
            }

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
