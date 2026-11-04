using System.Security.Claims;
using MenuScan.Models.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

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
            var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

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
            if (env.IsDevelopment())
            {
                var configuredAdminPassword = configuration["Seed:AdminPassword"];
                const string superAdminEmail = "admin@menuscan.com";
                var superAdmin = await userManager.FindByEmailAsync(superAdminEmail);
                if (superAdmin == null)
                {
                    if (!string.IsNullOrWhiteSpace(configuredAdminPassword))
                    {
                        superAdmin = new ApplicationUser
                        {
                            UserName = superAdminEmail,
                            Email = superAdminEmail,
                            FullName = "Platform Super Admin",
                            EmailConfirmed = true
                        };
                        var createResult = await userManager.CreateAsync(superAdmin, configuredAdminPassword);
                        if (createResult.Succeeded)
                        {
                            await userManager.AddToRoleAsync(superAdmin, AppRoles.SuperAdmin);
                        }
                    }
                    else
                    {
                        logger.LogWarning("SuperAdmin account not seeded because 'Seed:AdminPassword' is not configured.");
                    }
                }
                else
                {
                    // Detect known-weak passwords blocklist (for detection only, never to set)
                    string[] knownWeakAdminPasswords = ["Admin@123", "Admin@Secure2026!"];
                    bool hasLegacyPassword = false;
                    foreach (var weakPw in knownWeakAdminPasswords)
                    {
                        if (await userManager.CheckPasswordAsync(superAdmin, weakPw))
                        {
                            hasLegacyPassword = true;
                            break;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(configuredAdminPassword))
                    {
                        if (hasLegacyPassword || (superAdmin.LockoutEnd.HasValue && superAdmin.LockoutEnd.Value > DateTimeOffset.UtcNow))
                        {
                            var resetToken = await userManager.GeneratePasswordResetTokenAsync(superAdmin);
                            await userManager.ResetPasswordAsync(superAdmin, resetToken, configuredAdminPassword);
                            superAdmin.LockoutEnd = null;
                            await userManager.UpdateAsync(superAdmin);
                            logger.LogInformation("SuperAdmin password was successfully updated to configured Seed:AdminPassword and lockout cleared.");
                            await userManager.UpdateSecurityStampAsync(superAdmin);
                        }
                    }
                    else if (hasLegacyPassword)
                    {
                        var resetToken = await userManager.GeneratePasswordResetTokenAsync(superAdmin);
                        var randomPassword = $"A1!{Guid.NewGuid():N}{Guid.NewGuid():N}";
                        await userManager.ResetPasswordAsync(superAdmin, resetToken, randomPassword);
                        superAdmin.LockoutEnd = DateTimeOffset.UtcNow.AddYears(100);
                        await userManager.UpdateAsync(superAdmin);
                        logger.LogWarning("SuperAdmin account was using a known-weak password. It has been reset to an unlogged random password and locked out. Configure 'Seed:AdminPassword' or reset the password manually.");
                        await userManager.UpdateSecurityStampAsync(superAdmin);
                    }
                }
            }
            else
            {
                // Production: Provision initial SuperAdmin if none exists and credentials provided via configuration/environment variables
                var existingSuperAdmins = await userManager.GetUsersInRoleAsync(AppRoles.SuperAdmin);
                if (!existingSuperAdmins.Any())
                {
                    var prodEmail = configuration["Seed:AdminEmail"] ?? configuration["SuperAdmin:Email"];
                    var prodPassword = configuration["Seed:AdminPassword"] ?? configuration["SuperAdmin:Password"];

                    if (!string.IsNullOrWhiteSpace(prodEmail) && !string.IsNullOrWhiteSpace(prodPassword))
                    {
                        var prodAdmin = await userManager.FindByEmailAsync(prodEmail);
                        if (prodAdmin == null)
                        {
                            prodAdmin = new ApplicationUser
                            {
                                UserName = prodEmail,
                                Email = prodEmail,
                                FullName = "System Super Admin",
                                EmailConfirmed = true,
                                IsActive = true
                            };
                            var res = await userManager.CreateAsync(prodAdmin, prodPassword);
                            if (res.Succeeded)
                            {
                                await userManager.AddToRoleAsync(prodAdmin, AppRoles.SuperAdmin);
                                logger.LogInformation("Initial SuperAdmin account created successfully in production: {Email}", prodEmail);
                            }
                            else
                            {
                                logger.LogError("Failed to create initial SuperAdmin in production: {Errors}",
                                    string.Join("; ", res.Errors.Select(e => e.Description)));
                            }
                        }
                        else if (!await userManager.IsInRoleAsync(prodAdmin, AppRoles.SuperAdmin))
                        {
                            await userManager.AddToRoleAsync(prodAdmin, AppRoles.SuperAdmin);
                            logger.LogInformation("Promoted existing user {Email} to SuperAdmin role in production.", prodEmail);
                        }
                    }
                    else
                    {
                        logger.LogWarning("No SuperAdmin account exists in database. To create the initial SuperAdmin in production, supply 'Seed:AdminEmail' and 'Seed:AdminPassword' via configuration or environment variables.");
                    }
                }
            }

            // 3. Seed Demo Restaurants if none exists
            var restaurant = await context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Slug == "spice-symphony");
            if (restaurant == null)
            {
                restaurant = new Restaurant
                {
                    Name = "Brew & Bliss Coffee House",
                    Slug = "spice-symphony",
                    Address = "104 MG Road, Indiranagar, Bengaluru",
                    Phone = "+91 8799020418",
                    TaxNumber = "29ABCDE1234F1Z5",
                    DefaultTaxRate = 5.0m,
                    LogoUrl = "https://images.unsplash.com/photo-1501339847302-ac426a4a7cbb?auto=format&fit=crop&w=150&q=80",
                    IsActive = true
                };
                context.Restaurants.Add(restaurant);
                await context.SaveChangesAsync();
            }

            // Seed Restaurant B for multi-tenant isolation testing & demo ONLY when configured; clean up if disabled
            if (env.IsDevelopment())
            {
                var restaurantB = await context.Restaurants.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Slug == "tandoori-nights");
                if (configuration.GetValue<bool>("Seed:CreateTestRestaurant"))
                {
                    if (restaurantB == null)
                    {
                        restaurantB = new Restaurant
                        {
                            Name = "Tandoori Nights Grill",
                            Slug = "tandoori-nights",
                            Address = "42 Residency Road, Bengaluru",
                            Phone = "+91 9876543210",
                            TaxNumber = "29ZYXWV9876A1Z3",
                            DefaultTaxRate = 5.0m,
                            IsActive = true
                        };
                        context.Restaurants.Add(restaurantB);
                        await context.SaveChangesAsync();
                    }
                }
                else if (restaurantB != null)
                {
                    context.Restaurants.Remove(restaurantB);
                    await context.SaveChangesAsync();
                    logger.LogInformation("Cleaned up temporary test restaurant 'tandoori-nights' from development database.");
                }
            }

            // 4. Seed Restaurant Staff (Development only; passwords from config)
            if (env.IsDevelopment())
            {
                var configuredStaffPassword = configuration["Seed:StaffPassword"];

                async Task SeedStaffUser(string email, string name, string role)
                {
                    var user = await userManager.FindByEmailAsync(email);
                    if (user == null)
                    {
                        if (!string.IsNullOrWhiteSpace(configuredStaffPassword))
                        {
                            user = new ApplicationUser
                            {
                                UserName = email,
                                Email = email,
                                FullName = name,
                                RestaurantId = restaurant.Id,
                                EmailConfirmed = true,
                                IsActive = true
                            };
                            var res = await userManager.CreateAsync(user, configuredStaffPassword);
                            if (res.Succeeded)
                            {
                                await userManager.AddToRoleAsync(user, role);
                                await userManager.AddClaimAsync(user, new Claim("RestaurantId", restaurant.Id.ToString()));
                            }
                        }
                        else
                        {
                            logger.LogWarning("Staff user '{Email}' not seeded because 'Seed:StaffPassword' is not configured.", email);
                        }
                    }
                    else
                    {
                        // Detect known-weak passwords blocklist (for detection only, never to set)
                        string[] knownWeakStaffPasswords = ["Staff@123", "Staff@Secure2026!"];
                        bool hasLegacyPassword = false;
                        foreach (var weakPw in knownWeakStaffPasswords)
                        {
                            if (await userManager.CheckPasswordAsync(user, weakPw))
                            {
                                hasLegacyPassword = true;
                                break;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(configuredStaffPassword))
                        {
                            if (hasLegacyPassword || !user.IsActive)
                            {
                                var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
                                await userManager.ResetPasswordAsync(user, resetToken, configuredStaffPassword);
                                user.IsActive = true;
                                await userManager.UpdateAsync(user);
                                logger.LogInformation("Staff '{Email}' password was successfully updated to configured Seed:StaffPassword and reactivated.", email);
                                await userManager.UpdateSecurityStampAsync(user);
                            }
                        }
                        else if (hasLegacyPassword)
                        {
                            var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
                            var randomPassword = $"S1!{Guid.NewGuid():N}{Guid.NewGuid():N}";
                            await userManager.ResetPasswordAsync(user, resetToken, randomPassword);
                            user.IsActive = false;
                            await userManager.UpdateAsync(user);
                            logger.LogWarning("Staff account '{Email}' was using a known-weak password. It has been reset to an unlogged random password and deactivated. Configure 'Seed:StaffPassword' or activate and reset the password manually.", email);
                            await userManager.UpdateSecurityStampAsync(user);
                        }
                    }
                }

                await SeedStaffUser("manager@spicesymphony.com", "Cafe Manager", AppRoles.RestaurantAdmin);
                await SeedStaffUser("cashier@spicesymphony.com", "Main Cashier", AppRoles.Cashier);
                await SeedStaffUser("kitchen@spicesymphony.com", "Head Chef", AppRoles.KitchenStaff);
            }

            // 5. Seed Tables
            var existingTables = await context.DiningTables.IgnoreQueryFilters()
                .Where(t => t.RestaurantId == restaurant.Id)
                .ToListAsync();

            if (!existingTables.Any())
            {
                var tables = new List<DiningTable>
                {
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 1", Token = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), Capacity = 2 },
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 2", Token = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), Capacity = 4 },
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 3", Token = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), Capacity = 4 },
                    new() { RestaurantId = restaurant.Id, TableNumber = "Table 4", Token = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), Capacity = 6 }
                };
                context.DiningTables.AddRange(tables);
                await context.SaveChangesAsync();
            }

            // 6. Seed Categories & Rich Coffeehouse Menu Items
            var categories = await context.Categories.IgnoreQueryFilters()
                .Where(c => c.RestaurantId == restaurant.Id)
                .ToListAsync();

            var catCoffee = categories.FirstOrDefault(c => c.Name == "Coffee");
            if (catCoffee == null)
            {
                catCoffee = new Category { RestaurantId = restaurant.Id, Name = "Coffee", DisplayOrder = 1 };
                context.Categories.Add(catCoffee);
            }

            var catFood = categories.FirstOrDefault(c => c.Name == "Food & Sandwiches");
            if (catFood == null)
            {
                catFood = new Category { RestaurantId = restaurant.Id, Name = "Food & Sandwiches", DisplayOrder = 2 };
                context.Categories.Add(catFood);
            }

            var catPastry = categories.FirstOrDefault(c => c.Name == "Pastries & Desserts");
            if (catPastry == null)
            {
                catPastry = new Category { RestaurantId = restaurant.Id, Name = "Pastries & Desserts", DisplayOrder = 3 };
                context.Categories.Add(catPastry);
            }

            var catDrinks = categories.FirstOrDefault(c => c.Name == "Cold Drinks & Shakes");
            if (catDrinks == null)
            {
                catDrinks = new Category { RestaurantId = restaurant.Id, Name = "Cold Drinks & Shakes", DisplayOrder = 4 };
                context.Categories.Add(catDrinks);
            }

            await context.SaveChangesAsync();

            var existingItems = await context.MenuItems.IgnoreQueryFilters()
                .Where(m => m.RestaurantId == restaurant.Id)
                .ToListAsync();

            var targetItems = new List<(string Name, Guid CatId, string Desc, decimal Price, bool IsVeg, string Img, int Order)>
            {
                // Coffee
                ("Cappuccino", catCoffee.Id, "Smooth espresso with perfectly frothed steamed milk and delicate foam art", 150.00m, true,
                 "https://images.unsplash.com/photo-1572442388796-11668a67e53d?auto=format&fit=crop&w=600&q=80", 1),

                ("Coffee Latte", catCoffee.Id, "Rich espresso poured over silky steamed milk with subtle caramel notes", 160.00m, true,
                 "https://images.unsplash.com/photo-1517701604599-bb29b565090c?auto=format&fit=crop&w=600&q=80", 2),

                ("Americano", catCoffee.Id, "Bold espresso shots lengthened with pure hot water for a clean, rich taste", 130.00m, true,
                 "https://images.unsplash.com/photo-1514432324607-a09d9b4aefdd?auto=format&fit=crop&w=600&q=80", 3),

                ("V60 Artisan Pour Over", catCoffee.Id, "Single origin Arabica coffee hand-dripped over ice with bright fruity notes", 190.00m, true,
                 "https://images.unsplash.com/photo-1495474472287-4d71bcdd2085?auto=format&fit=crop&w=600&q=80", 4),

                // Food
                ("Grilled Chicken Club Sandwich", catFood.Id, "Grilled chicken breast, crispy bacon, farm egg, lettuce and herb mayo", 240.00m, false,
                 "https://images.unsplash.com/photo-1528735602780-2552fd46c7af?auto=format&fit=crop&w=600&q=80", 1),

                ("Loaded Cheesy Nachos", catFood.Id, "Crisp corn tortilla chips topped with melted cheddar, jalapenos, and salsa", 210.00m, true,
                 "https://images.unsplash.com/photo-1513456852971-30c0b8199d4d?auto=format&fit=crop&w=600&q=80", 2),

                ("Woodfired Margherita Pizza", catFood.Id, "San Marzano tomato sauce, fresh buffalo mozzarella, and fragrant basil", 290.00m, true,
                 "https://images.unsplash.com/photo-1574071318508-1cdbab80d002?auto=format&fit=crop&w=600&q=80", 3),

                ("Crispy Paneer Tikka", catFood.Id, "Tandoori charred paneer cubes with mint coriander chutney", 230.00m, true,
                 "https://images.unsplash.com/photo-1567188040759-fb8a883dc6d8?auto=format&fit=crop&w=600&q=80", 4),

                // Pastries
                ("Chocolate Muffin", catPastry.Id, "Rich, moist and perfectly baked chocolate chip muffin with dark cocoa", 120.00m, true,
                 "https://images.unsplash.com/photo-1607958996333-41aef7caefaa?auto=format&fit=crop&w=600&q=80", 1),

                ("Butter Croissant", catPastry.Id, "Flaky golden French pastry layered with rich Normandy butter", 110.00m, true,
                 "https://images.unsplash.com/photo-1555507036-ab1f4038808a?auto=format&fit=crop&w=600&q=80", 2),

                ("Sizzling Chocolate Brownie", catPastry.Id, "Warm walnut fudge brownie served with vanilla ice cream and chocolate drizzle", 180.00m, true,
                 "https://images.unsplash.com/photo-1564355808539-22fda35bed7e?auto=format&fit=crop&w=600&q=80", 3),

                // Cold Drinks
                ("Cold Brew Iced Coffee", catDrinks.Id, "Slow-steeped Arabica coffee served chilled over ice with fresh cream", 140.00m, true,
                 "https://images.unsplash.com/photo-1517701604599-bb29b565090c?auto=format&fit=crop&w=600&q=80", 1),

                ("Fresh Mint Lemonade", catDrinks.Id, "Fresh lemon juice, crushed spearmint, rock salt, and sparkling soda", 90.00m, true,
                 "https://images.unsplash.com/photo-1513558161293-cdaf765ed2fd?auto=format&fit=crop&w=600&q=80", 2)
            };

            foreach (var t in targetItems)
            {
                var existing = existingItems.FirstOrDefault(i => i.Name == t.Name);
                if (existing == null)
                {
                    context.MenuItems.Add(new MenuItem
                    {
                        RestaurantId = restaurant.Id,
                        CategoryId = t.CatId,
                        Name = t.Name,
                        Description = t.Desc,
                        Price = t.Price,
                        IsVeg = t.IsVeg,
                        ImageUrl = t.Img,
                        IsAvailable = true,
                        DisplayOrder = t.Order
                    });
                }
            }

            await context.SaveChangesAsync();

            // 7. Seed Item Customizations (Sizes & Add-ons)
            var existingCustoms = await context.ItemCustomizations.AnyAsync();
            if (!existingCustoms)
            {
                var newCustoms = new List<ItemCustomization>();
                var allItems = await context.MenuItems.IgnoreQueryFilters()
                    .Where(m => m.RestaurantId == restaurant.Id)
                    .ToListAsync();

                foreach (var item in allItems)
                {
                    if (item.CategoryId == catCoffee.Id || item.CategoryId == catDrinks.Id)
                    {
                        newCustoms.Add(new ItemCustomization { MenuItemId = item.Id, Name = "Large Size (+₹30)", Price = 30.00m, IsAvailable = true });
                        newCustoms.Add(new ItemCustomization { MenuItemId = item.Id, Name = "Oat Milk Substitute (+₹40)", Price = 40.00m, IsAvailable = true });
                        newCustoms.Add(new ItemCustomization { MenuItemId = item.Id, Name = "Extra Espresso Shot (+₹35)", Price = 35.00m, IsAvailable = true });
                        newCustoms.Add(new ItemCustomization { MenuItemId = item.Id, Name = "Vanilla / Caramel Drizzle (+₹25)", Price = 25.00m, IsAvailable = true });
                    }
                    else if (item.CategoryId == catFood.Id)
                    {
                        newCustoms.Add(new ItemCustomization { MenuItemId = item.Id, Name = "Extra Melted Cheese (+₹40)", Price = 40.00m, IsAvailable = true });
                        newCustoms.Add(new ItemCustomization { MenuItemId = item.Id, Name = "Spicy Jalapenos (+₹20)", Price = 20.00m, IsAvailable = true });
                        newCustoms.Add(new ItemCustomization { MenuItemId = item.Id, Name = "Herb Garlic Dip (+₹30)", Price = 30.00m, IsAvailable = true });
                    }
                    else if (item.CategoryId == catPastry.Id)
                    {
                        newCustoms.Add(new ItemCustomization { MenuItemId = item.Id, Name = "Vanilla Bean Ice Cream Scoop (+₹50)", Price = 50.00m, IsAvailable = true });
                        newCustoms.Add(new ItemCustomization { MenuItemId = item.Id, Name = "Warm Belgian Chocolate Drizzle (+₹30)", Price = 30.00m, IsAvailable = true });
                    }
                }

                if (newCustoms.Any())
                {
                    context.ItemCustomizations.AddRange(newCustoms);
                    await context.SaveChangesAsync();
                }
            }

            // 8. Seed Initial Sample Customer Feedbacks
            if (!await context.CustomerFeedbacks.AnyAsync())
            {
                var sampleOrders = await context.Orders.IgnoreQueryFilters()
                    .Where(o => o.RestaurantId == restaurant.Id)
                    .OrderBy(o => o.CreatedAtUtc)
                    .Take(3)
                    .ToListAsync();
                var sampleTable = await context.DiningTables.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.RestaurantId == restaurant.Id);

                if (sampleOrders.Count > 0 && sampleTable != null)
                {
                    var sampleFeedbacks = new List<CustomerFeedback>();

                    if (sampleOrders.Count >= 1)
                    {
                        sampleFeedbacks.Add(new CustomerFeedback
                        {
                            RestaurantId = restaurant.Id,
                            OrderId = sampleOrders[0].Id,
                            TableId = sampleTable.Id,
                            OverallRating = 5,
                            FoodRating = 5,
                            ServiceRating = 5,
                            CustomerName = "Aarav Sharma",
                            Tags = "☕ Great Coffee, ⚡ Fast Service, 🍕 Delicious Food",
                            Comment = "The artisan pour-over and woodfired pizza were phenomenal! Staff was super polite and attentive.",
                            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-45)
                        });
                    }

                    if (sampleOrders.Count >= 2)
                    {
                        sampleFeedbacks.Add(new CustomerFeedback
                        {
                            RestaurantId = restaurant.Id,
                            OrderId = sampleOrders[1].Id,
                            TableId = sampleTable.Id,
                            OverallRating = 5,
                            FoodRating = 5,
                            ServiceRating = 4,
                            CustomerName = "Priya Nair",
                            Tags = "🎶 Good Ambience, ☕ Great Coffee",
                            Comment = "Loved the cozy café ambience and smooth cold brew. Perfect place to work or catch up with friends.",
                            CreatedAtUtc = DateTime.UtcNow.AddHours(-2)
                        });
                    }

                    if (sampleOrders.Count >= 3)
                    {
                        sampleFeedbacks.Add(new CustomerFeedback
                        {
                            RestaurantId = restaurant.Id,
                            OrderId = sampleOrders[2].Id,
                            TableId = sampleTable.Id,
                            OverallRating = 4,
                            FoodRating = 4,
                            ServiceRating = 5,
                            CustomerName = "Vikram Mehta",
                            Tags = "⚡ Fast Service, 💰 Value for Money",
                            Comment = "Very smooth QR ordering on my phone. Sizzling brownie was delicious!",
                            CreatedAtUtc = DateTime.UtcNow.AddHours(-5)
                        });
                    }

                    context.CustomerFeedbacks.AddRange(sampleFeedbacks);
                    await context.SaveChangesAsync();
                }
            }
        }
    }
}
