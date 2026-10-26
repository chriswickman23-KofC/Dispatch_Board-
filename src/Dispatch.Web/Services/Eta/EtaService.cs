using Dispatch.Shared;
using Dispatch.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services.Eta;

public sealed class EtaService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    MapboxDirections directions,
    DispatchEvents events,
    DispatchClock clock,
    IConfiguration config)
{
    private TimeSpan PickupDuration => TimeSpan.FromMinutes(config.GetValue("Routing:PickupMinutes", 3));
    private TimeSpan DropoffDuration => TimeSpan.FromMinutes(config.GetValue("Routing:DropoffMinutes", 4));

    /// <summary>Recalculates ETAs for a driver's remaining stops in their current order. Returns false if it couldn't.</summary>
    public async Task<bool> RefreshDriverAsync(int driverId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var driver = await db.Drivers.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == driverId && d.Active && d.Status != DriverStatus.Offline, ct);
        if (driver?.CurrentLat is null || driver.LastPingAt < now.AddMinutes(-10)) return false;   // stale GPS = wrong ETAs

        var today = clock.Today;
        var routeId = await db.Routes
            .Where(r => r.DriverId == driverId && r.RouteDate == today)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync(ct);
        if (routeId is null) return false;

        var stops = await db.Stops
            .Include(s => s.Job)
            .Where(s => s.RouteId == routeId && (s.Status == StopStatus.Pending || s.Status == StopStatus.Arrived))
            .OrderBy(s => s.Sequence)
            .Take(MapboxDirections.MaxPoints - 1)
            .ToListAsync(ct);
        if (stops.Count == 0) return false;

        var t = now;
        var first = 0;
        var points = new List<(double Lat, double Lng)>();

        if (stops[0].Status == StopStatus.Arrived)
        {
            // Driver is standing at this stop: it's happening now, and the drive starts from here
            stops[0].EstimatedArrival = stops[0].ArrivedAt ?? now;
            t = now + ServiceTime(stops[0]);
            points.Add((stops[0].Lat, stops[0].Lng));
            first = 1;
        }
        else
        {
            points.Add((driver.CurrentLat.Value, driver.CurrentLng!.Value));
        }
        points.AddRange(stops.Skip(first).Select(s => (s.Lat, s.Lng)));

        var legs = await directions.GetLegDurationsAsync(points, ct);
        if (legs is null || legs.Count != points.Count - 1) return false;

        for (int i = first, leg = 0; i < stops.Count; i++, leg++)
        {
            t += legs[leg];
            if (stops[i].Kind == StopKind.Dropoff && stops[i].Job.WindowStart is { } windowStart && t < windowStart)
                t = windowStart;   // driver waits for the window to open
            stops[i].EstimatedArrival = t;
            t += ServiceTime(stops[i]);
        }

        await db.SaveChangesAsync(ct);
        events.Publish(new DispatchChange(ChangeKind.Etas, DriverId: driverId));
        return true;
    }

    private TimeSpan ServiceTime(Stop s) => s.Kind == StopKind.Pickup ? PickupDuration : DropoffDuration;
}
