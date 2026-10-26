using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Azure.Communication.Email;
using Azure.Communication.Sms;
using Azure.Storage.Blobs;
using Dispatch.Shared;
using Dispatch.Web.Data;
using Dispatch.Web.Endpoints;
using Dispatch.Web.Hubs;
using Dispatch.Web.Services;
using Dispatch.Web.Services.Calendar;
using Dispatch.Web.Services.Eta;
using Dispatch.Web.Services.Intake;
using Dispatch.Web.Services.Notifications;
using Dispatch.Web.Services.Routing;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Hosting;

/// <summary>
/// Everything this project adds on top of the Blazor template, in one place.
/// In Program.cs: builder.AddDispatch() before Build(), app.UseDispatchMiddleware() right after Build(),
/// and await app.MapDispatchAsync() before app.Run().
/// </summary>
public static class DispatchSetup
{
    public static WebApplicationBuilder AddDispatch(this WebApplicationBuilder builder)
    {
        builder.Configuration.AddJsonFile("appsettings.Dispatch.json", optional: true, reloadOnChange: true);
        var services = builder.Services;
        var config = builder.Configuration;

        // ---------- Database ----------
        // Blazor Server components must not share one DbContext across a long-lived circuit, so use a factory.
        var connectionString = config.GetConnectionString("Dispatch")
            ?? throw new InvalidOperationException("Connection string 'Dispatch' not found.");
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(connectionString));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());   // for Identity

        // ---------- Authentication for the driver app ----------
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromDays(3));   // invite links
        services.AddAuthentication().AddBearerToken(IdentityConstants.BearerScheme, o =>
        {
            o.BearerTokenExpiration = TimeSpan.FromHours(1);
            o.RefreshTokenExpiration = TimeSpan.FromDays(30);   // drivers stay signed in on company phones
            o.Events = new BearerTokenEvents
            {
                // SignalR's WebSocket connection sends the token in the query string
                OnMessageReceived = ctx =>
                {
                    var token = ctx.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs/driver"))
                        ctx.Token = token;
                    return Task.CompletedTask;
                },
            };
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(DriverApi.Policy, p => p.AddAuthenticationSchemes(IdentityConstants.BearerScheme).RequireRole(Roles.Driver))
            .AddPolicy("DispatchStaff", p => p.RequireRole(Roles.Admin, Roles.Dispatcher));

        // Enums travel as readable text so the app and server can't drift out of sync
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddSignalR();

        // ---------- Core dispatch ----------
        services.AddSingleton<DispatchEvents>();
        services.AddSingleton<DispatchClock>();
        services.AddScoped<DispatchService>();
        services.AddScoped<BoardQueries>();
        services.AddScoped<MapQueries>();
        services.AddScoped<JobDetailService>();
        services.AddScoped<ClientService>();
        services.AddScoped<PeopleService>();
        services.AddSingleton<PresenceTracker>();
        services.AddSingleton<SelectionSync>();
        services.AddSingleton<LocationService>();
        services.AddHostedService<LocationPurgeService>();
        services.AddHostedService<DriverNotifier>();
        if (builder.Environment.IsDevelopment())
            services.AddHostedService<DevGpsSimulator>();

        services.AddHttpClient<MapboxGeocoder>(c => c.BaseAddress = new Uri("https://api.mapbox.com/"));

        // ---------- Proof photos and sign-in keys ----------
        var storageConnection = config["Storage:ConnectionString"];
        if (!string.IsNullOrEmpty(storageConnection))
        {
            var container = new BlobContainerClient(storageConnection, "proofs");
            container.CreateIfNotExists();   // private by default
            services.AddSingleton(container);
            services.AddSingleton<IProofStorage, AzureBlobProofStorage>();

            // Keeps dispatcher logins and driver refresh tokens valid across deployments
            new BlobContainerClient(storageConnection, "dataprotection").CreateIfNotExists();
            services.AddDataProtection()
                .SetApplicationName("Dispatch")
                .PersistKeysToAzureBlobStorage(storageConnection, "dataprotection", "keys.xml");
        }
        else
        {
            services.AddSingleton<LocalProofStorage>();
            services.AddSingleton<IProofStorage>(sp => sp.GetRequiredService<LocalProofStorage>());
        }

        // ---------- Email ----------
        var emailConnection = config["Email:AcsConnectionString"];
        if (!string.IsNullOrEmpty(emailConnection))
        {
            services.AddSingleton(new EmailClient(emailConnection));
            services.AddSingleton<IAppEmailSender, AcsEmailSender>();
        }
        else
        {
            services.AddSingleton<IAppEmailSender, LogEmailSender>();
        }
        services.AddSingleton<IEmailSender<ApplicationUser>, IdentityEmailSender>();

        // ---------- Route optimization ----------
        services.AddHttpClient<IRouteOptimizer, GraphHopperOptimizer>(c =>
        {
            c.BaseAddress = new Uri("https://graphhopper.com/api/1/");
            c.Timeout = TimeSpan.FromSeconds(90);   // a full day's plan can take a while
        });
        services.AddScoped<RoutePlanningService>();

        // ---------- Live ETAs, customer texts, tracking ----------
        services.AddHttpClient<MapboxDirections>(c => c.BaseAddress = new Uri("https://api.mapbox.com/"));
        services.AddScoped<EtaService>();
        services.AddHostedService<EtaRefresher>();

        var smsConnection = config["Sms:AcsConnectionString"];
        if (!string.IsNullOrEmpty(smsConnection))
        {
            services.AddSingleton(new SmsClient(smsConnection));
            services.AddSingleton<ITextSender, AcsSmsSender>();
        }
        else
        {
            services.AddSingleton<ITextSender, LogTextSender>();   // texts stay off until SMS is configured
        }
        services.AddSingleton<CustomerNotifier>();
        services.AddHostedService(sp => sp.GetRequiredService<CustomerNotifier>());
        services.AddScoped<TrackingService>();

        // ---------- Online order intake ----------
        services.AddScoped<IntegrationService>();
        services.AddScoped<OrderIntakeService>();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("intake", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
        });

        // ---------- Closed days ----------
        services.AddSingleton<DeliveryCalendar>();
        services.AddScoped<ClosedDayService>();

        // Azure App Service sits behind a front end; use the real client address for rate limiting
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownNetworks.Clear();
            o.KnownProxies.Clear();
        });

        return builder;
    }

    public static WebApplication UseDispatchMiddleware(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseRateLimiter();
        return app;
    }

    public static async Task MapDispatchAsync(this WebApplication app)
    {
        app.MapDriverApi();
        app.MapHub<DriverHub>("/hubs/driver");
        app.MapTrackingEndpoints();
        app.MapIntakeEndpoints();

        // Development proof files, viewable only by signed-in dispatchers
        if (app.Services.GetService<LocalProofStorage>() is { } localProofs)
        {
            app.MapGet("/proof-files/{**path}", (string path) =>
                    localProofs.Resolve(path) is { } file ? Results.File(file.FullPath, file.ContentType) : Results.NotFound())
                .RequireAuthorization("DispatchStaff");
        }

        // Startup data (requires the database migrations to have been applied)
        await DevSeeder.SeedAsync(app.Services, app.Environment.IsDevelopment());
        await DevSeeder.BootstrapAdminAsync(app.Services);
        await ClosedDayService.SeedIfEmptyAsync(app.Services);
        if (app.Environment.IsDevelopment())
            await DevSeeder.SeedDriverLoginAsync(app.Services);
    }
}
