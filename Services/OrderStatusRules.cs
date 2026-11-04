using MenuScan.Models.Entities;

namespace MenuScan.Services
{
    public static class OrderStatusRules
    {
        private static readonly Dictionary<OrderStatus, HashSet<OrderStatus>> AllowedTransitions = new()
        {
            [OrderStatus.New] = new() { OrderStatus.Accepted, OrderStatus.Preparing, OrderStatus.Cancelled },
            [OrderStatus.Accepted] = new() { OrderStatus.Preparing, OrderStatus.Cancelled },
            [OrderStatus.Preparing] = new() { OrderStatus.Ready, OrderStatus.Cancelled },
            [OrderStatus.Ready] = new() { OrderStatus.Served },
            [OrderStatus.Served] = new() { OrderStatus.Completed },
            [OrderStatus.Completed] = new(), // Terminal
            [OrderStatus.Cancelled] = new()  // Terminal
        };

        /// <summary>
        /// Validates if transition from currentStatus to newStatus is allowed.
        /// </summary>
        public static bool IsValidTransition(OrderStatus currentStatus, OrderStatus newStatus)
        {
            if (!Enum.IsDefined(typeof(OrderStatus), currentStatus) || !Enum.IsDefined(typeof(OrderStatus), newStatus))
            {
                return false;
            }

            if (AllowedTransitions.TryGetValue(currentStatus, out var allowed))
            {
                return allowed.Contains(newStatus);
            }

            return false;
        }

        /// <summary>
        /// Returns whether the state can transition, giving a descriptive error message on failure.
        /// </summary>
        public static bool CanTransition(OrderStatus currentStatus, OrderStatus newStatus, out string? errorMessage)
        {
            if (!Enum.IsDefined(typeof(OrderStatus), newStatus))
            {
                errorMessage = "Invalid or undefined order status.";
                return false;
            }

            if (currentStatus == newStatus)
            {
                errorMessage = null;
                return true;
            }

            if (!IsValidTransition(currentStatus, newStatus))
            {
                if (currentStatus == OrderStatus.Completed)
                {
                    errorMessage = "Completed orders cannot be transitioned to any other status.";
                }
                else if (currentStatus == OrderStatus.Cancelled)
                {
                    errorMessage = "Cancelled orders cannot be transitioned to any other status.";
                }
                else
                {
                    errorMessage = $"Cannot transition order from '{currentStatus}' to '{newStatus}'.";
                }
                return false;
            }

            errorMessage = null;
            return true;
        }
    }
}
