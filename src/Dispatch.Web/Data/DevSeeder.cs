using Dispatch.Shared;
using Dispatch.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Data;

public static class DevSeeder
{
    /// <summary>Creates roles in every environment; in development, also sample clients, drivers, and jobs.</summary>
    public static async Task SeedAsync(IServiceProvider services, bool isDevelopment)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var roles = sp.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { Roles.Admin, Roles.Dispatcher, Roles.Driver })
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole(role));

        if (!isDevelopment) return;

        // Dispatcher login from user secrets (never hard-code passwords)
        var config = sp.GetRequiredService<IConfiguration>();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var email = config["Seed:DispatcherEmail"];
        var password = config["Seed:DispatcherPassword"];
        if (email is not null && password is not null && await users.FindByEmailAsync(email) is null)
        {
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Demo Dispatcher" };
            var created = await users.CreateAsync(user, password);
            if (created.Succeeded) await users.AddToRoleAsync(user, Roles.Dispatcher);
        }

        var dbFactory = sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var clock = sp.GetRequiredService<DispatchClock>();
        await using var db = await dbFactory.CreateDbContextAsync();

        if (!await db.Clients.AnyAsync())
        {
            db.Clients.AddRange(
                new Client { Name = "Bean & Co Cafe",    DefaultPickupAddress = "Clarendon, Arlington, VA",    DefaultPickupLat = 38.8868, DefaultPickupLng = -77.0954 },
                new Client { Name = "Greenleaf Florist", DefaultPickupAddress = "Shirlington, Arlington, VA",  DefaultPickupLat = 38.8418, DefaultPickupLng = -77.0870 },
                new Client { Name = "Sunrise Pharmacy",  DefaultPickupAddress = "Ballston, Arlington, VA",     DefaultPickupLat = 38.8822, DefaultPickupLng = -77.1112 },
                new Client { Name = "Harbor Grill",      DefaultPickupAddress = "Crystal City, Arlington, VA", DefaultPickupLat = 38.8573, DefaultPickupLng = -77.0505 });
            await db.SaveChangesAsync();
        }

        if (await db.Drivers.AnyAsync()) return;

        var now = DateTimeOffset.UtcNow;
        db.Drivers.AddRange(
            new Driver { Name = "Alex Kim",    ZoneId = 1, Status = DriverStatus.Available, CurrentLat = 38.889,  CurrentLng = -77.090, LastPingAt = now },
            new Driver { Name = "Bea Lopez",   ZoneId = 1, Status = DriverStatus.Available, CurrentLat = 38.895,  CurrentLng = -77.073, LastPingAt = now },
            new Driver { Name = "Carlos Diaz", ZoneId = 2, Status = DriverStatus.Available, CurrentLat = 38.862,  CurrentLng = -77.088, LastPingAt = now.AddMinutes(-7) },
            new Driver { Name = "Dana Wu",     ZoneId = 2, Status = DriverStatus.Available, CurrentLat = 38.8418, CurrentLng = -77.087, LastPingAt = now },
            new Driver { Name = "Eli Brooks",  ZoneId = 1, Status = DriverStatus.Offline,   CurrentLat = 38.883,  CurrentLng = -77.103, LastPingAt = now.AddMinutes(-95) });

        Job MakeJob(JobType type, int zoneId, string recipient, string pickup, double pLat, double pLng,
                    string dropoff, double dLat, double dLng, int minutesAgo) => new()
        {
            Type = type,
            ZoneId = zoneId,
            RecipientName = recipient,
            ScheduledDate = type == JobType.Scheduled ? clock.Today : null,
            CreatedAt = now.AddMinutes(-minutesAgo),
            UpdatedAt = now,
            Stops =
            {
                new Stop { Kind = StopKind.Pickup, Address = pickup, Lat = pLat, Lng = pLng },
                new Stop { Kind = StopKind.Dropoff, Address = dropoff, Lat = dLat, Lng = dLng },
            },
            Events = { new JobEvent { ActorName = "Seeder", Action = "created", Description = "Created (sample data)" } },
        };

        db.Jobs.AddRange(
            MakeJob(JobType.Scheduled, 1, "Bean & Co Cafe",    "Clarendon, Arlington, VA",     38.8868, -77.0954, "Rosslyn, Arlington, VA",       38.8964, -77.0716, 42),
            MakeJob(JobType.OnDemand,  1, "R. Patel",          "Courthouse, Arlington, VA",    38.8907, -77.0846, "Lyon Village, Arlington, VA",  38.8945, -77.0947, 34),
            MakeJob(JobType.Scheduled, 2, "Greenleaf Florist", "Shirlington, Arlington, VA",   38.8418, -77.0870, "Crystal City, Arlington, VA",  38.8573, -77.0505, 20),
            MakeJob(JobType.OnDemand,  2, "T. Nguyen",         "Columbia Pike, Arlington, VA", 38.862,  -77.0880, "Pentagon City, Arlington, VA", 38.8627, -77.0596, 18),
            MakeJob(JobType.OnDemand,  1, "Sunrise Pharmacy",  "Ballston, Arlington, VA",      38.8822, -77.1112, "Cherrydale, Arlington, VA",    38.8977, -77.1080, 6));

        await db.SaveChangesAsync();
    }

    /// <summary>Development only: links a driver login (from user secrets) to Alex Kim.</summary>
    public static async Task SeedDriverLoginAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var config = sp.GetRequiredService<IConfiguration>();
        var email = config["Seed:DriverEmail"];
        var password = config["Seed:DriverPassword"];
        if (email is null || password is null) return;

        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Alex Kim" };
            if (!(await users.CreateAsync(user, password)).Succeeded) return;
        }
        if (!await users.IsInRoleAsync(user, Roles.Driver))
            await users.AddToRoleAsync(user, Roles.Driver);

        var dbFactory = sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Drivers
            .Where(d => d.Name == "Alex Kim" && d.UserId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.UserId, user.Id));
    }

    /// <summary>Creates the first admin from configuration if none exists yet. Remove the settings afterwards.</summary>
    public static async Task BootstrapAdminAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var config = sp.GetRequiredService<IConfiguration>();
        var email = config["Bootstrap:AdminEmail"];
        var password = config["Bootstrap:AdminPassword"];
        if (email is null || password is null) return;

        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        if ((await users.GetUsersInRoleAsync(Roles.Admin)).Count > 0) return;

        var admin = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Administrator" };
        if ((await users.CreateAsync(admin, password)).Succeeded)
            await users.AddToRoleAsync(admin, Roles.Admin);
    }
}
