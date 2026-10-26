using Dispatch.Shared;
using Dispatch.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services;

public sealed record MapDriverVm(int Id, string Name, DriverStatus Status, int? ZoneId, double? Lat, double? Lng, DateTimeOffset? LastPingAt);

public sealed record MapStopVm(int Id, StopKind Kind, string Address, double Lat, double Lng,
    int? RouteId, int? Sequence, StopStatus Status, string? FailureReason);

public sealed record MapJobVm(int Id, string Ref, JobType Type, JobStatus Status, int? ZoneId,
    DateTimeOffset CreatedAt, DateTimeOffset? WindowEnd, int? DriverId, string DisplayName,
    bool NeedsReview, IReadOnlyList<MapStopVm> Stops)
{
    public MapStopVm? Pickup => Stops.FirstOrDefault(s => s.Kind == StopKind.Pickup);
    public MapStopVm? Dropoff => Stops.FirstOrDefault(s => s.Kind == StopKind.Dropoff);
}

public sealed record MapSnapshot(
    IReadOnlyList<ZoneVm> Zones,
    IReadOnlyList<MapDriverVm> Drivers,
    IReadOnlyList<MapJobVm> Jobs,
    IReadOnlyList<RouteVm> Routes)
{
    public static MapSnapshot Empty { get; } = new([], [], [], []);
}

public sealed class MapQueries(IDbContextFactory<ApplicationDbContext> dbFactory, DispatchClock clock)
{
    public async Task<MapSnapshot> LoadAsync()
    {
        var today = clock.Today;
        await using var db = await dbFactory.CreateDbContextAsync();

        var zones = await db.Zones.AsNoTracking().OrderBy(z => z.Name)
            .Select(z => new ZoneVm(z.Id, z.Name)).ToListAsync();

        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => ((j.Status == JobStatus.Unassigned || j.Status == JobStatus.Assigned || j.Status == JobStatus.InProgress)
                         && (j.ScheduledDate == null || j.ScheduledDate <= today))
                        || (j.Status == JobStatus.Failed && j.ReviewedAt == null))
            .Select(j => new MapJobVm(
                j.Id, j.Ref, j.Type, j.Status, j.ZoneId, j.CreatedAt, j.WindowEnd, j.DriverId,
                j.Client != null ? j.Client.Name : (j.RecipientName ?? "No name"),
                j.Status == JobStatus.Failed && j.ReviewedAt == null,
                j.Stops.Select(s => new MapStopVm(s.Id, s.Kind, s.Address, s.Lat, s.Lng, s.RouteId, s.Sequence, s.Status, s.FailureReason)).ToList()))
            .ToListAsync();

        var drivers = await QueryDrivers(db).ToListAsync();

        var routes = await db.Routes.AsNoTracking()
            .Where(r => r.RouteDate == today)
            .Select(r => new RouteVm(r.Id, r.DriverId))
            .ToListAsync();

        return new MapSnapshot(zones, drivers, jobs, routes);
    }

    public async Task<List<MapDriverVm>> LoadDriversAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await QueryDrivers(db).ToListAsync();
    }

    /// <summary>Where the driver has been since midnight Arlington time.</summary>
    public async Task<List<double[]>> GetTrailAsync(int driverId)
    {
        var since = clock.StartOfTodayUtc;
        await using var db = await dbFactory.CreateDbContextAsync();
        var points = await db.DriverLocations.AsNoTracking()
            .Where(l => l.DriverId == driverId && l.RecordedAt >= since)
            .OrderBy(l => l.RecordedAt)
            .Take(5000)
            .Select(l => new { l.Lat, l.Lng })
            .ToListAsync();
        return points.Select(p => new[] { p.Lat, p.Lng }).ToList();
    }

    private static IQueryable<MapDriverVm> QueryDrivers(ApplicationDbContext db) =>
        db.Drivers.AsNoTracking()
            .Where(d => d.Active)
            .OrderBy(d => d.Name)
            .Select(d => new MapDriverVm(d.Id, d.Name, d.Status, d.ZoneId, d.CurrentLat, d.CurrentLng, d.LastPingAt));
}
