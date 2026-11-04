using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace MenuScan.Services
{
    public interface ITenantService
    {
        Guid? CurrentRestaurantId { get; }
        void SetCurrentRestaurant(Guid restaurantId);
    }

    public class TenantService : ITenantService
    {
        public const string TenantCookieName = "menuscan_selected_tenant";
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IDataProtector _protector;
        private Guid? _explicitRestaurantId;

        public TenantService(IHttpContextAccessor httpContextAccessor, IDataProtectionProvider dataProtectionProvider)
        {
            _httpContextAccessor = httpContextAccessor;
            _protector = dataProtectionProvider.CreateProtector("MenuScan.TenantService.v1");
        }

        public Guid? CurrentRestaurantId
        {
            get
            {
                if (_explicitRestaurantId.HasValue)
                {
                    return _explicitRestaurantId;
                }

                var httpContext = _httpContextAccessor.HttpContext;
                var user = httpContext?.User;

                // 1. Non-SuperAdmin user must use ONLY the RestaurantId claim, never the cookie.
                if (user?.Identity?.IsAuthenticated == true && !user.IsInRole("SuperAdmin"))
                {
                    var claim = user.FindFirst("RestaurantId");
                    if (claim != null && Guid.TryParse(claim.Value, out var tenantId))
                    {
                        return tenantId;
                    }
                    return null;
                }

                // 2. Accept tenant cookie only for SuperAdmin, protected with IDataProtector
                if (user?.Identity?.IsAuthenticated == true && user.IsInRole("SuperAdmin"))
                {
                    if (httpContext != null && httpContext.Request.Cookies.TryGetValue(TenantCookieName, out var cookieVal) && !string.IsNullOrWhiteSpace(cookieVal))
                    {
                        try
                        {
                            var unprotected = _protector.Unprotect(cookieVal);
                            if (Guid.TryParse(unprotected, out var cookieTenantId))
                            {
                                return cookieTenantId;
                            }
                        }
                        catch
                        {
                            // Tampered or invalid cookie
                        }
                    }

                    // Fallback to claim if user has one
                    var claim = user.FindFirst("RestaurantId");
                    if (claim != null && Guid.TryParse(claim.Value, out var tenantId))
                    {
                        return tenantId;
                    }
                }

                return null;
            }
        }

        public void SetCurrentRestaurant(Guid restaurantId)
        {
            _explicitRestaurantId = restaurantId;
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext != null)
            {
                var protectedValue = _protector.Protect(restaurantId.ToString());
                httpContext.Response.Cookies.Append(TenantCookieName, protectedValue, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.AddDays(30),
                    IsEssential = true
                });
            }
        }
    }
}
