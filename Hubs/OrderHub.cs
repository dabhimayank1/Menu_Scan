using MenuScan.Models.Entities;
using Microsoft.AspNetCore.SignalR;

namespace MenuScan.Hubs
{
    public class OrderHub : Hub
    {
        private void ValidateRestaurantGroupAccess(string restaurantId)
        {
            var user = Context.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                throw new HubException("Unauthorized: Anonymous diners cannot access restaurant groups.");
            }

            if (user.IsInRole(AppRoles.SuperAdmin))
            {
                return;
            }

            var userRestaurantId = user.FindFirst("RestaurantId")?.Value;
            if (string.IsNullOrWhiteSpace(userRestaurantId) ||
                !string.Equals(userRestaurantId, restaurantId, StringComparison.OrdinalIgnoreCase))
            {
                throw new HubException("Access denied: You can only access your own restaurant's feed.");
            }
        }

        public async Task JoinRestaurantGroup(string restaurantId)
        {
            ValidateRestaurantGroupAccess(restaurantId);
            await Groups.AddToGroupAsync(Context.ConnectionId, $"Restaurant_{restaurantId}");
        }

        public async Task LeaveRestaurantGroup(string restaurantId)
        {
            ValidateRestaurantGroupAccess(restaurantId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"Restaurant_{restaurantId}");
        }

        public async Task JoinOrderGroup(string orderToken)
        {
            if (!string.IsNullOrWhiteSpace(orderToken))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"Order_{orderToken}");
            }
        }
    }
}
