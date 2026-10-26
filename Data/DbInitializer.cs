using System.Security.Claims;
using MenuScan.Models.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MenuScan.Data
{
    public static class DbInitializer
    {
        public static async Task SeedAsync(IServiceProvider serviceProvider)
        {
            using var scope = serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            await context.Database.MigrateAsync();

            // 1. Seed Roles
            string[] roles = [AppRoles.SuperAdmin, AppRoles.RestaurantAdmin, AppRoles.Cashier, AppRoles.KitchenStaff];
            foreach (var role in roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    await roleManager.CreateAsync(new IdentityRole(role));
                }
            }

            // 2. Seed Super Admin
            const string superAdminEmail = "admin@menuscan.com";
            var superAdmin = await userManager.FindByEmailAsync(superAdminEmail);
            if (superAdmin == null)
            {
                superAdmin = new ApplicationUser
                {
                    UserName = superAdminEmail,
                    Email = superAdminEmail,
                    FullName = "Platform Super Admin",
                    EmailConfirmed = true
                };
                var createResult = await userManager.CreateAsync(superAdmin, "Admin@123");
                if (createResult.Succeeded)
                {
                    await userManager.AddToRoleAsync(superAdmin, AppRoles.SuperAdmin);
                }
            }

            // 3. Seed Demo Restaurant if none exists
            var restaurant = await context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Slug == "spice-symphony");
            if (restaurant == null)
            {
                restaurant = new Restaurant
                {
                    Name = "Spice Symphony Cafe",
                    Slug = "spice-symphony",
                    Address = "104 MG Road, Indiranagar, Bengaluru",
                    Phone = "+91 98765 43210",
                    TaxNumber = "29ABCDE1234F1Z5",
                    DefaultTaxRate = 5.0m,
                    IsActive = true
                };
                context.Restaurants.Add(restaurant);
                await context.SaveChangesAsync();
            }

            // 4. Seed Restaurant Staff
            async Task SeedStaffUser(string email, string name, string role)
            {
                var user = await userManager.FindByEmailAsync(email);
                if (user == null)
                {
                    user = new ApplicationUser
                    {
                        UserName = email,
                        Email = email,
                        FullName = name,
                        RestaurantId = restaurant.Id,
                        EmailConfirmed = true
                    };
                    var res = await userManager.CreateAsync(user, "Staff@123");
                    if (res.Succeeded)
                    {
                        await userManager.AddToRoleAsync(user, role);
                        await userManager.AddClaimAsync(user, new Claim("RestaurantId", restaurant.Id.ToString()));
                    }
                }
            }

            await SeedStaffUser("manager@spicesymphony.com", "Cafe Manager", AppRoles.RestaurantAdmin);
            await SeedStaffUser("cashier@spicesymphony.com", "Main Cashier", AppRoles.Cashier);
            await SeedStaffUser("kitchen@spicesymphony.com", "Head Chef", AppRoles.KitchenStaff);

            // 5. Seed Tables
            var existingTables = await context.DiningTables.IgnoreQueryFilters()
                .Where(t => t.RestaurantId == restaurant.Id)
                .ToListAsync();

            if (!existingTables.Any())
            {
                var tables = new List<DiningTable>
                {
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 1", Token = "TAB-01", Capacity = 2 },
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 2", Token = "TAB-02", Capacity = 4 },
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 3", Token = "TAB-03", Capacity = 4 },
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 4", Token = "TAB-04", Capacity = 6 }
                };
                context.DiningTables.AddRange(tables);
                await context.SaveChangesAsync();
            }

            // 6. Seed Categories & Menu Items
            var existingCategories = await context.Categories.IgnoreQueryFilters()
                .Where(c => c.RestaurantId == restaurant.Id)
                .ToListAsync();

            if (!existingCategories.Any())
            {
                var catStarters = new Category { RestaurantId = restaurant.Id, Name = "Starters & Appetizers", DisplayOrder = 1 };
                var catMains = new Category { RestaurantId = restaurant.Id, Name = "Main Course", DisplayOrder = 2 };
                var catBeverages = new Category { RestaurantId = restaurant.Id, Name = "Beverages & Shakes", DisplayOrder = 3 };
                var catDesserts = new Category { RestaurantId = restaurant.Id, Name = "Desserts", DisplayOrder = 4 };

                context.Categories.AddRange(catStarters, catMains, catBeverages, catDesserts);
                await context.SaveChangesAsync();

                var items = new List<MenuItem>
                {
                    new()
                    {
                        RestaurantId = restaurant.Id,
                        CategoryId = catStarters.Id,
                        Name = "Crispy Paneer Tikka",
                        Description = "Cottage cheese marinated in tandoori spices and charred in clay oven",
                        Price = 240.00m,
                        IsVeg = true,
                        IsAvailable = true,
                        DisplayOrder = 1
                    },
                    new()
                    {
                        RestaurantId = restaurant.Id,
                        CategoryId = catStarters.Id,
                        Name = "Loaded Cheesy Nachos",
                        Description = "Tortilla chips topped with jalapenos, salsa, beans, and melted cheddar sauce",
                        Price = 210.00m,
                        IsVeg = true,
                        IsAvailable = true,
                        DisplayOrder = 2
                    },
                    new()
                    {
                        RestaurantId = restaurant.Id,
                        CategoryId = catMains.Id,
                        Name = "Butter Paneer Masala & Naan Combo",
                        Description = "Rich tomato cashew gravy paneer served with 2 butter garlic naans",
                        Price = 320.00m,
                        IsVeg = true,
                        IsAvailable = true,
                        DisplayOrder = 1
                    },
                    new()
                    {
                        RestaurantId = restaurant.Id,
                        CategoryId = catMains.Id,
                        Name = "Woodfired Margherita Pizza (10\")",
                        Description = "San Marzano tomatoes, fresh mozzarella, basil, and extra virgin olive oil",
                        Price = 290.00m,
                        IsVeg = true,
                        IsAvailable = true,
                        DisplayOrder = 2
                    },
                    new()
                    {
                        RestaurantId = restaurant.Id,
                        CategoryId = catBeverages.Id,
                        Name = "Cold Brew Iced Coffee",
                        Description = "Slow-steeped Arabica coffee served chilled over ice with vanilla cream",
                        Price = 140.00m,
                        IsVeg = true,
                        IsAvailable = true,
                        DisplayOrder = 1
                    },
                    new()
                    {
                        RestaurantId = restaurant.Id,
                        CategoryId = catBeverages.Id,
                        Name = "Fresh Mint Lemonade",
                        Description = "Freshly squeezed lemon juice, crushed mint, rock salt, and soda",
                        Price = 90.00m,
                        IsVeg = true,
                        IsAvailable = true,
                        DisplayOrder = 2
                    },
                    new()
                    {
                        RestaurantId = restaurant.Id,
                        CategoryId = catDesserts.Id,
                        Name = "Sizzling Chocolate Brownie with Ice Cream",
                        Description = "Warm fudge brownie served on a hot skillet with vanilla bean ice cream and melted chocolate",
                        Price = 180.00m,
                        IsVeg = true,
                        IsAvailable = true,
                        DisplayOrder = 1
                    }
                };

                context.MenuItems.AddRange(items);
                await context.SaveChangesAsync();
            }
        }
    }
}
