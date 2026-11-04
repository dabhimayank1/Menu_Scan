using MenuScan.Models.Entities;

namespace MenuScan.Models.ViewModels
{
    public class MonthlySalesViewModel
    {
        public Restaurant Restaurant { get; set; } = default!;
        public int Year { get; set; }
        public int Month { get; set; }
        public string MonthName => new DateTime(Year, Month, 1).ToString("MMMM yyyy");

        // KPI Summary
        public decimal TotalSales { get; set; }
        public decimal TotalTax { get; set; }
        public int TotalBills { get; set; }
        public decimal AvgOrderValue { get; set; }
        public decimal DailyAverageSales { get; set; }
        public int ActiveSalesDays { get; set; }
        public decimal HighestDaySales { get; set; }
        public DateTime? HighestSalesDate { get; set; }

        // Payment Method breakdown
        public decimal UpiTotal { get; set; }
        public decimal CashTotal { get; set; }
        public decimal CardTotal { get; set; }

        // Day-by-Day summary
        public List<DailySalesSummaryDto> DailyBreakdowns { get; set; } = new();

        // Top Selling Items of the Month
        public List<TopItemDto> TopSellingItems { get; set; } = new();

        // Category breakdown
        public List<CategorySalesDto> CategoryBreakdowns { get; set; } = new();

        // Cashier & Billing reconciliation breakdown
        public List<CashierSalesSummaryDto> CashierBreakdowns { get; set; } = new();

        // Chart Data
        public List<string> DayLabels { get; set; } = new();
        public List<decimal> DaySalesData { get; set; } = new();
        public List<int> DayOrdersData { get; set; } = new();

        // Navigation helpers
        public DateTime SelectedDate => new DateTime(Year, Month, 1);
        public DateTime PrevMonth => SelectedDate.AddMonths(-1);
        public DateTime NextMonth => SelectedDate.AddMonths(1);
    }

    public class CashierSalesSummaryDto
    {
        public string CashierName { get; set; } = string.Empty;
        public int BillsCount { get; set; }
        public decimal CashCollected { get; set; }
        public decimal UpiCollected { get; set; }
        public decimal CardCollected { get; set; }
        public decimal TotalRevenue { get; set; }
        public double PercentageOfTotal { get; set; }
    }

    public class DailySalesSummaryDto
    {
        public DateTime Date { get; set; }
        public int OrdersCount { get; set; }
        public decimal GrossSales { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal NetSales { get; set; }
        public decimal UpiAmount { get; set; }
        public decimal CashAmount { get; set; }
        public decimal CardAmount { get; set; }
    }

    public class TopItemDto
    {
        public string ItemName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Revenue { get; set; }
        public double PercentageOfTotal { get; set; }
    }

    public class CategorySalesDto
    {
        public string CategoryName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Revenue { get; set; }
        public double PercentageOfTotal { get; set; }
    }
}
