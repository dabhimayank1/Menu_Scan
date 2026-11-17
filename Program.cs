using System.Threading.RateLimiting;
using MenuScan.Data;
using MenuScan.Hubs;
using MenuScan.Models.Entities;
using MenuScan.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 1. Database & Multi-Tenant Setup
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ITimeHelper, TimeHelper>();
builder.Services.AddScoped<ITenantService, TenantService>();
builder.Services.AddTransient<IQRService, QRService>();

// 2. Identity Configuration
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequiredLength = 6;
    options.User.RequireUniqueEmail = true;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.AllowedForNewUsers = true;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

var enforceHttps = builder.Configuration.GetValue<bool>("App:EnforceHttps");
var httpsPort = builder.Configuration.GetValue<int?>("HTTPS_PORT") 
    ?? builder.Configuration.GetValue<int?>("ANCM_HTTPS_PORT");
var hasHttpsConfigured = enforceHttps || (httpsPort.HasValue && httpsPort.Value > 0);

if (hasHttpsConfigured)
{
    builder.Services.AddHttpsRedirection(options =>
    {
        options.HttpsPort = httpsPort ?? 443;
        options.RedirectStatusCode = StatusCodes.Status307TemporaryRedirect;
    });
    builder.Services.AddHsts(options =>
    {
        options.MaxAge = TimeSpan.FromDays(365);
        options.IncludeSubDomains = true;
        options.ExcludedHosts.Clear();
    });
}

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = hasHttpsConfigured
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;
});

var securityStampSeconds = builder.Configuration.GetValue<int?>("App:SecurityStampValidationSeconds") ?? 60;
Console.WriteLine($"[CONFIG] App:SecurityStampValidationSeconds = {securityStampSeconds}");
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    options.ValidationInterval = TimeSpan.FromSeconds(securityStampSeconds);
});

// 3. MVC, Antiforgery & SignalR
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "RequestVerificationToken";
});

builder.Services.AddSignalR();

// Rate limiting policies
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Strict policy for login to prevent brute force credential attacks
    options.AddPolicy("login-policy", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var isDev = builder.Environment.IsDevelopment();
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = isDev ? 50 : 5,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });

    // Flexible policy for diner APIs (accommodates multiple tables/customers sharing restaurant guest Wi-Fi NAT)
    options.AddPolicy("diner-api-policy", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var isDev = builder.Environment.IsDevelopment();
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = isDev ? 200 : 60,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });

    // Backward-compatible alias
    options.AddPolicy("fixed-by-ip", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });
});

var app = builder.Build();
TimeHelper.Current = app.Services.GetRequiredService<ITimeHelper>();

// 4. Automatic DB Migration & Data Seeding
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        logger.LogInformation("Initializing database and seeding default data...");
        await DbInitializer.SeedAsync(scope.ServiceProvider);
        logger.LogInformation("Database initialization complete.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "A critical error occurred while seeding the database.");
        throw;
    }
}

// 5. HTTP Pipeline: Configure ForwardedHeaders only if trusted proxies or networks are explicitly configured
var knownProxies = builder.Configuration.GetSection("App:KnownProxies").Get<string[]>();
var knownNetworks = builder.Configuration.GetSection("App:KnownNetworks").Get<string[]>();
var validProxies = knownProxies?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
var validNetworks = knownNetworks?.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();

if ((validProxies != null && validProxies.Length > 0) || (validNetworks != null && validNetworks.Length > 0))
{
    var forwardedHeadersOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    };

    if (validProxies != null)
    {
        foreach (var proxy in validProxies)
        {
            if (System.Net.IPAddress.TryParse(proxy.Trim(), out var ip))
            {
                forwardedHeadersOptions.KnownProxies.Add(ip);
                if (System.Net.IPAddress.IsLoopback(ip))
                {
                    forwardedHeadersOptions.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
                    if (System.Net.IPAddress.TryParse("::ffff:127.0.0.1", out var v4Mapped))
                    {
                        forwardedHeadersOptions.KnownProxies.Add(v4Mapped);
                    }
                }
            }
        }
    }

    if (validNetworks != null)
    {
        foreach (var network in validNetworks)
        {
            var parts = network.Trim().Split('/');
            if (parts.Length == 2 && System.Net.IPAddress.TryParse(parts[0], out var prefix) && int.TryParse(parts[1], out var prefixLength))
            {
                forwardedHeadersOptions.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength));
            }
        }
    }

    app.UseForwardedHeaders(forwardedHeadersOptions);
}

MenuScan.Services.FileHelper.Initialize(app.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot"));

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    if (hasHttpsConfigured)
    {
        app.UseHsts();
    }
}

if (hasHttpsConfigured)
{
    app.UseHttpsRedirection();
}

// API error handling & RFC-7807 problem+json response middleware for all /api endpoints
var enableTestHooks = app.Configuration.GetValue<bool>("App:EnableTestHooks");
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        try
        {
            if (enableTestHooks && context.Request.Path == "/api/test/force-unhandled-exception")
            {
                throw new InvalidOperationException("Simulated API failure for exception testing");
            }

            await next();

            if (context.Response.StatusCode == StatusCodes.Status404NotFound && !context.Response.HasStarted)
            {
                context.Response.ContentType = "application/problem+json";
                var problem = new
                {
                    type = "https://tools.ietf.org/html/rfc7231#section-6.5.4",
                    title = "Not Found",
                    status = 404,
                    detail = $"The requested endpoint '{context.Request.Path}' was not found.",
                    traceId = context.TraceIdentifier
                };
                await context.Response.WriteAsJsonAsync(problem, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json");
            }
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "Unhandled API exception for {Path}", context.Request.Path);

            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/problem+json";

                var isDev = app.Environment.IsDevelopment();
                var problem = new
                {
                    type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
                    title = "An error occurred while processing your request.",
                    status = 500,
                    detail = isDev ? ex.Message : "An unexpected server error occurred.",
                    traceId = context.TraceIdentifier
                };
                await context.Response.WriteAsJsonAsync(problem, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json");
            }
        }
    }
    else
    {
        if (enableTestHooks && context.Request.Path == "/test/force-unhandled-exception")
        {
            throw new InvalidOperationException("Simulated MVC failure for exception testing");
        }

        await next();
    }
});

app.UseStaticFiles();

app.UseRouting();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// 6. SignalR Hub Mapping
app.MapHub<OrderHub>("/hubs/order");

// 7. Route Mapping
app.MapControllerRoute(
    name: "customerTableRoute",
    pattern: "r/{slug}/t/{token}",
    defaults: new { controller = "Customer", action = "TableMenu" });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
