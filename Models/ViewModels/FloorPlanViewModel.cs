using MenuScan.Models.Entities;

namespace MenuScan.Models.ViewModels
{
    public class FloorPlanViewModel
    {
        public Restaurant Restaurant { get; set; } = null!;
        public List<FloorTableDto> Tables { get; set; } = new();

        public int TotalTables => Tables.Count;
        public int AvailableCount => Tables.Count(t => t.FloorStatus == FloorTableStatus.Available);
        public int OccupiedCount => Tables.Count(t => t.FloorStatus != FloorTableStatus.Available);
        public int FoodReadyCount => Tables.Count(t => t.HasFoodReady);
        public int AssistanceCount => Tables.Count(t => t.HasAssistanceCall || t.HasBillCall);
        public int OccupancyRate => TotalTables == 0 ? 0 : (int)Math.Round((double)OccupiedCount / TotalTables * 100);
    }

    public enum FloorTableStatus
    {
        Available,
        OccupiedNew,
        OccupiedPreparing,
        FoodReady,
        OccupiedServed,
        AssistanceCalled,
        BillRequested
    }

    public class FloorTableDto
    {
        public Guid TableId { get; set; }
        public string TableNumber { get; set; } = string.Empty;
        public int Capacity { get; set; }
        public string Token { get; set; } = string.Empty;
        public FloorTableStatus FloorStatus { get; set; } = FloorTableStatus.Available;

        public bool HasFoodReady { get; set; }
        public bool HasAssistanceCall { get; set; }
        public bool HasBillCall { get; set; }

        public Guid? ActiveOrderId { get; set; }
        public string? ActiveOrderNumber { get; set; }
        public OrderStatus? ActiveOrderStatus { get; set; }
        public decimal ActiveOrderTotal { get; set; }
        public DateTime? OrderCreatedAtUtc { get; set; }
        public int SeatedDurationMinutes { get; set; }

        public List<FloorOrderItemDto> OrderItems { get; set; } = new();
        public List<FloorServiceRequestDto> PendingRequests { get; set; } = new();
    }

    public class FloorOrderItemDto
    {
        public string ItemName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public string? CustomizationSummary { get; set; }
    }

    public class FloorServiceRequestDto
    {
        public Guid RequestId { get; set; }
        public string RequestType { get; set; } = string.Empty;
        public string? Notes { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string TimeAgo { get; set; } = string.Empty;
    }
}
