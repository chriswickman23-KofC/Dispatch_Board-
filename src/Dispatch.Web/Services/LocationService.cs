using Dispatch.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services;

public sealed record LocationPoint(double Lat, double Lng, float? AccuracyM, DateTimeOffset RecordedAt);

public sealed class LocationService(IDbContextFactory<ApplicationDbContext> dbFactory, DispatchEvents events)
{
    public async Task RecordAsync(int driverId, IReadOnlyCollection<LocationPoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        db.DriverLocations.AddRange(points.Select(p => new DriverLocation
        {
            DriverId = driverId,
            Lat = p.Lat,
            Lng = p.Lng,
            AccuracyM = p.AccuracyM,
            RecordedAt = p.RecordedAt.ToUniversalTime(),
        }));
        await db.SaveChangesAsync(ct);

        // Only move the pin forward in time, so delayed offline batches can't drag it backwards
        var latest = points.MaxBy(p => p.RecordedAt)!;
        var latestUtc = latest.RecordedAt.ToUniversalTime();
        await db.Drivers
            .Where(d => d.Id == driverId && (d.LastPingAt == null || d.LastPingAt < latestUtc))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.CurrentLat, latest.Lat)
                .SetProperty(d => d.CurrentLng, latest.Lng)
                .SetProperty(d => d.LastPingAt, latestUtc), ct);

        events.Publish(new DispatchChange(ChangeKind.Locations, DriverId: driverId));
    }
}
