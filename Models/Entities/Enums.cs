namespace MenuScan.Models.Entities
{
    public enum OrderStatus
    {
        New = 0,
        Accepted = 1,
        Preparing = 2,
        Ready = 3,
        Served = 4,
        Cancelled = 5,
        Completed = 6
    }

    public enum BillStatus
    {
        Unpaid = 0,
        Paid = 1,
        Cancelled = 2
    }

    public enum PaymentMethod
    {
        Cash = 0,
        UPI = 1,
        Card = 2,
        Other = 3
    }

    public static class AppRoles
    {
        public const string SuperAdmin = "SuperAdmin";
        public const string RestaurantAdmin = "RestaurantAdmin";
        public const string Cashier = "Cashier";
        public const string KitchenStaff = "KitchenStaff";
    }
}
