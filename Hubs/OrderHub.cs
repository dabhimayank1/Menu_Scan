using Microsoft.AspNetCore.SignalR;

namespace MenuScan.Hubs
{
    public class OrderHub : Hub
    {
        public async Task JoinRestaurantGroup(string restaurantId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"Restaurant_{restaurantId}");
        }

        public async Task LeaveRestaurantGroup(string restaurantId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"Restaurant_{restaurantId}");
        }

        public async Task JoinOrderGroup(string orderToken)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"Order_{orderToken}");
        }
    }
}
