using Dispatch.Shared;
using Dispatch.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services;

/// <summary>
/// Development only: moves on-shift sample drivers toward their next stop so the map can be tested
/// without phones. Drivers with stale GPS or a real app login are left alone.
/// </summary>
public sealed class DevGpsSimulator(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    LocationService locations,
    DispatchClock clock,
    ILogger<DevGpsSimulator> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var now = DateTimeOffset.UtcNow;
                var freshCutoff = now.AddMinutes(-5);
                var today = clock.Today;

                var drivers = await db.Drivers.AsNoTracking()
                    .Where(d => d.Active && d.Status != DriverStatus.Offline && d.CurrentLat != null
                                && d.LastPingAt > freshCutoff && d.UserId == null)
                    .ToListAsync(ct);

                foreach (var d in drivers)
                {
                    var routeId = await db.Routes
                        .Where(r => r.DriverId == d.Id && r.RouteDate == today)
                        .Select(r => (int?)r.Id)
                        .FirstOrDefaultAsync(ct);

                    var next = routeId is null ? null : await db.Stops.AsNoTracking()
                        .Where(s => s.RouteId == routeId && (s.Status == StopStatus.Pending || s.Status == StopStatus.Arrived))
                        .OrderBy(s => s.Sequence)
                        .FirstOrDefaultAsync(ct);

                    double lat = d.CurrentLat!.Value, lng = d.CurrentLng!.Value;
                    if (next is { Status: StopStatus.Pending })
                    {
                        lat += (next.Lat - lat) * 0.08;
                        lng += (next.Lng - lng) * 0.08;
                    }
                    await locations.RecordAsync(d.Id, [new LocationPoint(lat, lng, 10, now)], ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "GPS simulator tick failed");
            }
        }
    }
}
