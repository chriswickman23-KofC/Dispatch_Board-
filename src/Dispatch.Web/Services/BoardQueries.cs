using Dispatch.Shared;
using Dispatch.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services;

public sealed class BoardQueries(IDbContextFactory<ApplicationDbContext> dbFactory, DispatchClock clock)
{
    public async Task<BoardSnapshot> LoadAsync()
    {
        var today = clock.Today;
        await using var db = await dbFactory.CreateDbContextAsync();

        var zones = await db.Zones.AsNoTracking()
            .OrderBy(z => z.Name)
            .Select(z => new ZoneVm(z.Id, z.Name))
            .ToListAsync();

        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => (j.Status == JobStatus.Unassigned || j.Status == JobStatus.Assigned || j.Status == JobStatus.InProgress)
                        && (j.ScheduledDate == null || j.ScheduledDate <= today))
            .Select(j => new JobVm(
                j.Id, j.Ref, j.Type, j.Status, j.ZoneId, j.ScheduledDate, j.WindowStart, j.WindowEnd,
                j.CreatedAt, j.DriverId,
                j.Client != null ? j.Client.Name : (j.RecipientName ?? "No name"),
                j.Stops.Select(s => new StopVm(s.Id, s.JobId, s.Kind, s.Address, s.RouteId, s.Sequence, s.Status, s.EstimatedArrival)).ToList()))
            .ToListAsync();

        var drivers = await QueryDrivers(db).ToListAsync();

        var routes = await db.Routes.AsNoTracking()
            .Where(r => r.RouteDate == today)
            .Select(r => new RouteVm(r.Id, r.DriverId))
            .ToListAsync();

        // Failed jobs nobody has dealt with yet, from any day
        var failedJobs = await db.Jobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Failed && j.ReviewedAt == null)
            .Select(j => new
            {
                j.Id, j.Ref, j.ZoneId,
                Name = j.Client != null ? j.Client.Name : (j.RecipientName ?? "No name"),
                DriverName = j.Driver != null ? j.Driver.Name : null,
                Failed = j.Stops
                    .Where(s => s.Status == StopStatus.Failed)
                    .OrderBy(s => s.CompletedAt)
                    .ThenBy(s => s.Kind == StopKind.Pickup ? 0 : 1)   // a failed pickup also fails its dropoff
                    .Select(s => new { s.Kind, s.Address, s.FailureReason, s.CompletedAt })
                    .FirstOrDefault(),
            })
            .ToListAsync();

        var attention = failedJobs
            .Where(j => j.Failed is not null)
            .OrderByDescending(j => j.Failed!.CompletedAt)
            .Select(j => new AttentionVm(j.Id, j.Ref, j.ZoneId, j.Name, j.DriverName,
                j.Failed!.Kind, j.Failed.Address, j.Failed.FailureReason, j.Failed.CompletedAt))
            .ToList();

        // Dropoffs completed since midnight Arlington time
        var since = clock.StartOfTodayUtc;
        var delivered = await db.Stops.AsNoTracking()
            .Where(s => s.Kind == StopKind.Dropoff && s.Status == StopStatus.Completed && s.CompletedAt >= since)
            .OrderByDescending(s => s.CompletedAt)
            .Select(s => new DeliveredVm(
                s.JobId, s.Job.Ref, s.Job.ZoneId,
                s.Job.Client != null ? s.Job.Client.Name : (s.Job.RecipientName ?? ""),
                s.Job.Driver != null ? s.Job.Driver.Name : null,
                s.Address, s.CompletedAt!.Value,
                s.Proofs.Any(p => p.PhotoPath != null),
                s.Proofs.Any(p => p.SignaturePath != null),
                s.Proofs.Select(p => p.RecipientName).FirstOrDefault()))
            .ToListAsync();

        return new BoardSnapshot(zones, jobs, drivers, routes, attention, delivered);
    }

    /// <summary>Cheap refresh for GPS pings and driver status changes.</summary>
    public async Task<List<DriverVm>> LoadDriversAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await QueryDrivers(db).ToListAsync();
    }

    public async Task<UserProfile> GetProfileAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserProfile(u.FullName != "" ? u.FullName : (u.Email ?? "Dispatcher"), u.ZoneId))
            .FirstAsync();
    }

    private static IQueryable<DriverVm> QueryDrivers(ApplicationDbContext db) =>
        db.Drivers.AsNoTracking()
            .Where(d => d.Active)
            .OrderBy(d => d.Name)
            .Select(d => new DriverVm(d.Id, d.Name, d.Status, d.ZoneId, d.LastPingAt));
}
