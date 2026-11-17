using MenuScan.Models.Entities;
using MenuScan.Services;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MenuScan.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        private readonly ITenantService _tenantService;

        public ApplicationDbContext(
            DbContextOptions<ApplicationDbContext> options,
            ITenantService tenantService) : base(options)
        {
            _tenantService = tenantService;
        }

        public DbSet<Restaurant> Restaurants => Set<Restaurant>();
        public DbSet<DiningTable> DiningTables => Set<DiningTable>();
        public DbSet<Category> Categories => Set<Category>();
        public DbSet<MenuItem> MenuItems => Set<MenuItem>();
        public DbSet<ItemCustomization> ItemCustomizations => Set<ItemCustomization>();
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<OrderItem> OrderItems => Set<OrderItem>();
        public DbSet<OrderStatusHistory> OrderStatusHistories => Set<OrderStatusHistory>();
        public DbSet<Bill> Bills => Set<Bill>();
        public DbSet<Payment> Payments => Set<Payment>();
        public DbSet<ServiceRequest> ServiceRequests => Set<ServiceRequest>();
        public DbSet<CustomerFeedback> CustomerFeedbacks => Set<CustomerFeedback>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // Unique indexes
            builder.Entity<Restaurant>()
                .HasIndex(r => r.Slug)
                .IsUnique();

            builder.Entity<DiningTable>()
                .HasIndex(t => t.Token)
                .IsUnique();

            builder.Entity<DiningTable>()
                .HasIndex(t => new { t.RestaurantId, t.TableNumber })
                .IsUnique()
                .HasFilter("[IsActive] = 1");

            builder.Entity<Category>()
                .HasIndex(c => new { c.RestaurantId, c.Name })
                .IsUnique()
                .HasFilter("[IsActive] = 1");

            builder.Entity<Order>()
                .HasIndex(o => o.OrderNumber)
                .IsUnique();

            builder.Entity<Order>()
                .HasIndex(o => o.OrderToken)
                .IsUnique();

            builder.Entity<Order>()
                .HasIndex(o => new { o.RestaurantId, o.IdempotencyKey })
                .IsUnique();

            builder.Entity<Bill>()
                .HasIndex(b => b.BillNumber)
                .IsUnique();

            // Configure Delete Behaviors to avoid multiple cascade paths in SQL Server
            builder.Entity<DiningTable>()
                .HasOne(t => t.Restaurant)
                .WithMany(r => r.Tables)
                .HasForeignKey(t => t.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Category>()
                .HasOne(c => c.Restaurant)
                .WithMany(r => r.Categories)
                .HasForeignKey(c => c.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<MenuItem>()
                .HasOne(m => m.Restaurant)
                .WithMany(r => r.MenuItems)
                .HasForeignKey(m => m.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<MenuItem>()
                .HasOne(m => m.Category)
                .WithMany(c => c.MenuItems)
                .HasForeignKey(m => m.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Order>()
                .HasOne(o => o.Restaurant)
                .WithMany(r => r.Orders)
                .HasForeignKey(o => o.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Order>()
                .HasOne(o => o.Table)
                .WithMany(t => t.Orders)
                .HasForeignKey(o => o.TableId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Bill>()
                .HasOne(b => b.Restaurant)
                .WithMany()
                .HasForeignKey(b => b.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Bill>()
                .HasOne(b => b.Order)
                .WithOne(o => o.Bill)
                .HasForeignKey<Bill>(b => b.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            // Multi-tenant Global Query Filters
            builder.Entity<Category>()
                .HasQueryFilter(e => _tenantService.CurrentRestaurantId == null || e.RestaurantId == _tenantService.CurrentRestaurantId);

            builder.Entity<MenuItem>()
                .HasQueryFilter(e => _tenantService.CurrentRestaurantId == null || e.RestaurantId == _tenantService.CurrentRestaurantId);

            builder.Entity<ItemCustomization>()
                .HasQueryFilter(e => _tenantService.CurrentRestaurantId == null || e.MenuItem!.RestaurantId == _tenantService.CurrentRestaurantId);

            builder.Entity<DiningTable>()
                .HasQueryFilter(e => _tenantService.CurrentRestaurantId == null || e.RestaurantId == _tenantService.CurrentRestaurantId);

            builder.Entity<Order>()
                .HasQueryFilter(e => _tenantService.CurrentRestaurantId == null || e.RestaurantId == _tenantService.CurrentRestaurantId);

            builder.Entity<OrderItem>()
                .HasQueryFilter(e => _tenantService.CurrentRestaurantId == null || e.Order!.RestaurantId == _tenantService.CurrentRestaurantId);

            builder.Entity<OrderStatusHistory>()
                .HasQueryFilter(e => _tenantService.CurrentRestaurantId == null || e.Order!.RestaurantId == _tenantService.CurrentRestaurantId);

            builder.Entity<Bill>()
                .HasQueryFilter(e => _tenantService.CurrentRestaurantId == null || e.RestaurantId == _tenantService.CurrentRestaurantId);

            builder.Entity<Payment>()
                .HasQueryFilter(e => _tenantService.CurrentRestaurantId == null || e.Bill!.RestaurantId == _tenantService.CurrentRestaurantId);

            builder.Entity<ServiceRequest>()
                .HasOne(s => s.Restaurant)
                .WithMany()
                .HasForeignKey(s => s.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<ServiceRequest>()
                .HasOne(s => s.Table)
                .WithMany()
                .HasForeignKey(s => s.TableId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<ServiceRequest>()
                .HasOne(s => s.Order)
                .WithMany()
                .HasForeignKey(s => s.OrderId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.Entity<ServiceRequest>()
                .HasQueryFilter(e => _tenantService.CurrentRestaurantId == null || e.RestaurantId == _tenantService.CurrentRestaurantId);

            builder.Entity<CustomerFeedback>()
                .HasIndex(f => f.OrderId)
                .IsUnique();

            builder.Entity<CustomerFeedback>()
                .HasOne(f => f.Restaurant)
                .WithMany()
                .HasForeignKey(f => f.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<CustomerFeedback>()
                .HasOne(f => f.Order)
                .WithMany()
                .HasForeignKey(f => f.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<CustomerFeedback>()
                .HasOne(f => f.Table)
                .WithMany()
                .HasForeignKey(f => f.TableId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.Entity<CustomerFeedback>()
                .HasQueryFilter(e => _tenantService.CurrentRestaurantId == null || e.RestaurantId == _tenantService.CurrentRestaurantId);
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var tenantId = _tenantService.CurrentRestaurantId;
            if (tenantId.HasValue)
            {
                foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
                {
                    if (entry.State == EntityState.Added && entry.Entity.RestaurantId == Guid.Empty)
                    {
                        entry.Entity.RestaurantId = tenantId.Value;
                    }
                }
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
