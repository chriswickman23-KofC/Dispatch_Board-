using System.Collections.Concurrent;
using Dispatch.Shared;
using Dispatch.Web.Data;
using Dispatch.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services;

/// <summary>Tells a driver's phone (over SignalR) the moment dispatch changes their route.</summary>
public sealed class DriverNotifier(
    DispatchEvents events,
    IHubContext<DriverHub> hub,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<DriverNotifier> log) : IHostedService
{
    private readonly ConcurrentDictionary<int, string> _userIds = new();

    public Task StartAsync(CancellationToken ct)
    {
        events.Changed += OnChangedAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        events.Changed -= OnChangedAsync;
        return Task.CompletedTask;
    }

    private async Task OnChangedAsync(DispatchChange change)
    {
        if (change.Kind != ChangeKind.Jobs || change.DriverId is not int driverId) return;
        try
        {
            if (!_userIds.TryGetValue(driverId, out var userId))
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                userId = await db.Drivers.Where(d => d.Id == driverId).Select(d => d.UserId).FirstOrDefaultAsync();
                if (userId is null) return;   // driver has no app login yet
                _userIds[driverId] = userId;
            }
            // SignalR matches users by their NameIdentifier claim, which is the Identity user id
            await hub.Clients.User(userId).SendAsync(DriverHubEvents.RouteChanged);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Couldn't notify driver {DriverId}", driverId);
        }
    }
}
