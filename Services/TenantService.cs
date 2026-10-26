using System.Security.Claims;
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
        private readonly IHttpContextAccessor _httpContextAccessor;
        private Guid? _explicitRestaurantId;

        public TenantService(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public Guid? CurrentRestaurantId
        {
            get
            {
                if (_explicitRestaurantId.HasValue)
                {
                    return _explicitRestaurantId;
                }

                var user = _httpContextAccessor.HttpContext?.User;
                if (user?.Identity?.IsAuthenticated == true)
                {
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
        }
    }
}
