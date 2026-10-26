using Dispatch.Shared;
using Dispatch.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services;

public sealed record NewJob(
    JobType Type,
    GeoPoint Pickup,
    GeoPoint Dropoff,
    int? ClientId = null,
    string? RecipientName = null,
    string? RecipientPhone = null,
    int? ZoneId = null,
    DateOnly? ScheduledDate = null,
    DateTimeOffset? WindowStart = null,
    DateTimeOffset? WindowEnd = null,
    string? Notes = null,
    JobSource Source = JobSource.Manual,
    string? ExternalId = null);

public sealed record ImportResult(int Created, int Skipped);

/// <summary>All dispatch rules live here: safe assignment, pickup-before-dropoff, failure handling.</summary>
public sealed class DispatchService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    DispatchEvents events,
    DispatchClock clock)
{
    // ---------- Creating jobs ----------

    public async Task<Result<Job>> CreateJobAsync(NewJob input, Actor actor)
    {
        if (input.Type == JobType.Scheduled && input.ScheduledDate is null)
            return Result<Job>.Fail("Scheduled jobs need a date.");
        if (input.WindowStart is { } ws && input.WindowEnd is { } we && we <= ws)
            return Result<Job>.Fail("Window end must be after start.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var job = BuildJob(input, actor);
        db.Jobs.Add(job);
        await db.SaveChangesAsync();   // Ref is filled in by the database

        events.Publish(new DispatchChange(ChangeKind.Jobs, job.Id));
        return Result<Job>.Ok(job);
    }

    /// <summary>Imports a batch in one transaction; rows whose order number was already imported are skipped.</summary>
    public async Task<Result<ImportResult>> ImportJobsAsync(int? clientId, int? zoneId, IReadOnlyList<NewJob> jobs, Actor actor)
    {
        if (jobs.Count > 500) return Result<ImportResult>.Fail("Too many rows (500 max per import).");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var incomingIds = jobs.Select(j => j.ExternalId?.Trim())
            .Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).ToList();

        var alreadyImported = await db.Jobs
            .Where(j => j.ClientId == clientId && j.Source == JobSource.Csv && j.ExternalId != null && incomingIds.Contains(j.ExternalId))
            .Select(j => j.ExternalId!)
            .ToListAsync();

        var seen = new HashSet<string>(alreadyImported);
        int created = 0, skipped = 0;

        foreach (var input in jobs)
        {
            var externalId = input.ExternalId?.Trim();
            if (!string.IsNullOrEmpty(externalId) && !seen.Add(externalId))
            {
                skipped++;   // duplicate in the database or earlier in this same file
                continue;
            }
            db.Jobs.Add(BuildJob(input with { ClientId = clientId, ZoneId = zoneId, Source = JobSource.Csv }, actor));
            created++;
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();

        events.Publish(new DispatchChange(ChangeKind.Jobs));
        return Result<ImportResult>.Ok(new ImportResult(created, skipped));
    }

    // ---------- Assignment ----------

    /// <summary>Only succeeds if the job is still unassigned, so two dispatchers can't double-assign.</summary>
    public async Task<Result> AssignAsync(int jobId, int driverId, Actor actor)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var driver = await db.Drivers.FindAsync(driverId);
        if (driver is null) return Result.Fail("Driver not found.");
        if (driver.Status == DriverStatus.Offline) return Result.Fail($"{driver.Name} is offline.");

        // Atomic claim: Postgres locks the row, so a second dispatcher's update matches zero rows
        var claimed = await db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Unassigned)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Assigned)
                .SetProperty(j => j.DriverId, driverId)
                .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow));

        if (claimed == 0)
        {
            var by = await db.JobEvents
                .Where(e => e.JobId == jobId && e.Action == "assigned")
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => e.ActorName)
                .FirstOrDefaultAsync();
            return Result.Fail($"Already assigned by {by ?? "another dispatcher"}.");
        }

        var job = await db.Jobs.Include(j => j.Stops).FirstAsync(j => j.Id == jobId);
        // Overdue scheduled jobs land on today's route, never a past date
        var routeDate = job.ScheduledDate is { } scheduled && scheduled > clock.Today ? scheduled : clock.Today;

        var route = await db.Routes.FirstOrDefaultAsync(r => r.DriverId == driverId && r.RouteDate == routeDate);
        if (route is null)
        {
            route = new DriverRoute { DriverId = driverId, RouteDate = routeDate };
            db.Routes.Add(route);
            await db.SaveChangesAsync();
        }

        var next = await db.Stops.Where(s => s.RouteId == route.Id).MaxAsync(s => (int?)s.Sequence) ?? 0;
        foreach (var stop in job.Stops)
        {
            stop.RouteId = route.Id;
            stop.Sequence = next + (stop.Kind == StopKind.Pickup ? 1 : 2);
        }
        job.RouteId = route.Id;
        driver.Status = DriverStatus.Busy;

        db.JobEvents.Add(new JobEvent
        {
            JobId = jobId, ActorId = actor.UserId, ActorName = actor.Name,
            Action = "assigned", Description = $"Assigned to {driver.Name}",
        });

        await db.SaveChangesAsync();
        await tx.CommitAsync();

        events.Publish(new DispatchChange(ChangeKind.Jobs, jobId, driverId));
        return Result.Ok();
    }

    public async Task<Result> UnassignAsync(int jobId, Actor actor)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var job = await db.Jobs.Include(j => j.Stops).FirstOrDefaultAsync(j => j.Id == jobId);
        if (job is null || job.Status != JobStatus.Assigned || job.DriverId is not int driverId)
            return Result.Fail("Only assigned jobs that haven't started can be returned to the queue.");

        job.Status = JobStatus.Unassigned;
        job.DriverId = null;
        job.RouteId = null;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        foreach (var stop in job.Stops)
        {
            stop.RouteId = null;
            stop.Sequence = null;
            stop.EstimatedArrival = null;
        }

        db.JobEvents.Add(new JobEvent
        {
            JobId = jobId, ActorId = actor.UserId, ActorName = actor.Name,
            Action = "unassigned", Description = "Returned to queue",
        });

        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Result.Fail("This job was just changed by someone else. Please try again."); }

        await RefreshDriverStatusAsync(db, driverId);
        events.Publish(new DispatchChange(ChangeKind.Jobs, jobId, driverId));
        return Result.Ok();
    }

    /// <summary>Rejects any order that puts a dropoff before its own pickup.</summary>
    public async Task<Result> ReorderRouteAsync(int routeId, IReadOnlyList<int> stopIds)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var stops = await db.Stops.Where(s => s.RouteId == routeId).ToListAsync();
        var order = stopIds.Select((id, i) => (id, seq: i + 1)).ToDictionary(x => x.id, x => x.seq);

        foreach (var stop in stops)
            if (order.TryGetValue(stop.Id, out var seq)) stop.Sequence = seq;

        var invalid = stops.GroupBy(s => s.JobId).Any(g =>
        {
            var pickup = g.FirstOrDefault(s => s.Kind == StopKind.Pickup);
            var dropoff = g.FirstOrDefault(s => s.Kind == StopKind.Dropoff);
            return pickup is not null && dropoff is not null && dropoff.Sequence < pickup.Sequence;
        });
        if (invalid) return Result.Fail("Pickup must come before its dropoff.");

        // A new order makes old estimates wrong; optimization and the ETA refresher set fresh ones
        foreach (var stop in stops) stop.EstimatedArrival = null;

        await db.SaveChangesAsync();
        var driverId = await db.Routes.Where(r => r.Id == routeId).Select(r => r.DriverId).FirstAsync();
        events.Publish(new DispatchChange(ChangeKind.Jobs, DriverId: driverId));
        return Result.Ok();
    }

    // ---------- Stop updates (driver app and dispatchers) ----------

    public async Task<Result> UpdateStopStatusAsync(int stopId, StopStatus status, string? reason, Actor actor)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var stop = await db.Stops
            .Include(s => s.Job).ThenInclude(j => j.Stops)
            .FirstOrDefaultAsync(s => s.Id == stopId);

        if (stop is null || (actor.DriverId is int me && stop.Job.DriverId != me))
            return Result.Fail("Stop not found or not on your route.");

        var job = stop.Job;
        if (status == StopStatus.Completed && stop.Kind == StopKind.Dropoff &&
            job.Stops.Any(s => s.Kind == StopKind.Pickup && s.Status != StopStatus.Completed))
        {
            return Result.Fail("Complete the pickup for this job first.");
        }

        var now = DateTimeOffset.UtcNow;
        stop.Status = status;
        if (status == StopStatus.Arrived) stop.ArrivedAt = now;
        if (status is StopStatus.Completed or StopStatus.Failed) stop.CompletedAt = now;
        stop.FailureReason = reason;

        if (status == StopStatus.Failed)
        {
            // A failed pickup means the dropoff can't happen either
            foreach (var other in job.Stops.Where(s => s.Id != stopId && (s.Status is StopStatus.Pending or StopStatus.Arrived)))
            {
                other.Status = StopStatus.Failed;
                other.CompletedAt = now;
                other.FailureReason = reason ?? "Pickup failed";
            }
            job.Status = JobStatus.Failed;
            job.ReviewedAt = null;      // puts it in "Needs attention"
            job.ReviewedById = null;
        }
        else if (status == StopStatus.Completed)
        {
            job.Status = stop.Kind == StopKind.Pickup ? JobStatus.InProgress : JobStatus.Delivered;
        }
        job.UpdatedAt = now;

        var what = stop.Kind == StopKind.Pickup ? "Pickup" : "Delivery";
        db.JobEvents.Add(new JobEvent
        {
            JobId = job.Id, ActorId = actor.UserId, ActorName = actor.Name,
            Action = $"stop_{status.ToString().ToLowerInvariant()}",
            Description = status switch
            {
                StopStatus.Arrived => $"Arrived at {stop.Kind.ToString().ToLowerInvariant()}",
                StopStatus.Completed => stop.Kind == StopKind.Pickup ? "Picked up" : "Delivered",
                StopStatus.Failed => $"{what} failed: {reason ?? "no reason given"}",
                _ => status.ToString(),
            },
        });

        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Result.Fail("This job was just changed by someone else. Please try again."); }

        if ((status is StopStatus.Completed or StopStatus.Failed) && job.DriverId is int driverId)
            await RefreshDriverStatusAsync(db, driverId);

        events.Publish(new DispatchChange(ChangeKind.Jobs, job.Id, job.DriverId));
        return Result.Ok();
    }

    public async Task<DriverStatus> SetShiftAsync(int driverId, bool onShift)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var hasActive = await db.Jobs.AnyAsync(j =>
            j.DriverId == driverId && (j.Status == JobStatus.Assigned || j.Status == JobStatus.InProgress));

        var status = !onShift ? DriverStatus.Offline : hasActive ? DriverStatus.Busy : DriverStatus.Available;
        await db.Drivers.Where(d => d.Id == driverId).ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status));

        events.Publish(new DispatchChange(ChangeKind.Drivers, DriverId: driverId));
        return status;
    }

    // ---------- Failed jobs ----------

    /// <summary>Back to the queue on a new date, keeping the same delivery window times.</summary>
    public async Task<Result> RescheduleFailedJobAsync(int jobId, DateOnly newDate, string? note, Actor actor)
    {
        if (newDate < clock.Today) return Result.Fail("Pick today or a future date.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var job = await db.Jobs.Include(j => j.Stops).FirstOrDefaultAsync(j => j.Id == jobId);
        if (job is null || job.Status != JobStatus.Failed) return Result.Fail("Only failed jobs can be rescheduled.");

        var previousReason = job.Stops
            .Where(s => s.Status == StopStatus.Failed)
            .OrderBy(s => s.CompletedAt)
            .Select(s => s.FailureReason)
            .FirstOrDefault();
        var previousDriverId = job.DriverId;

        job.Status = JobStatus.Unassigned;
        job.Type = JobType.Scheduled;
        job.ScheduledDate = newDate;
        job.WindowStart = ShiftToDate(job.WindowStart, newDate);
        job.WindowEnd = ShiftToDate(job.WindowEnd, newDate);
        job.DriverId = null;
        job.RouteId = null;
        job.ReviewedAt = null;
        job.ReviewedById = null;
        job.UpdatedAt = DateTimeOffset.UtcNow;

        foreach (var stop in job.Stops)
        {
            stop.Status = StopStatus.Pending;
            stop.RouteId = null;
            stop.Sequence = null;
            stop.ArrivedAt = null;
            stop.CompletedAt = null;
            stop.FailureReason = null;
            stop.EstimatedArrival = null;
        }

        var description = $"Rescheduled to {newDate:MMM d}"
            + (previousReason is null ? "" : $" (failed: {previousReason})")
            + (string.IsNullOrWhiteSpace(note) ? "" : $": \"{note.Trim()}\"");
        db.JobEvents.Add(new JobEvent
        {
            JobId = jobId, ActorId = actor.UserId, ActorName = actor.Name,
            Action = "rescheduled", Description = description,
        });

        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Result.Fail("This job was just changed by someone else. Please try again."); }

        if (previousDriverId is int driverId) await RefreshDriverStatusAsync(db, driverId);
        events.Publish(new DispatchChange(ChangeKind.Jobs, jobId, previousDriverId));   // also removes it from the driver's phone
        return Result.Ok();
    }

    /// <summary>Takes a failed job out of "Needs attention" without retrying it. A note is required.</summary>
    public async Task<Result> CloseFailedJobAsync(int jobId, string note, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(note)) return Result.Fail("Add a note explaining why the job is closed.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var closed = await db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Failed && j.ReviewedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.ReviewedAt, now)
                .SetProperty(j => j.ReviewedById, actor.UserId)
                .SetProperty(j => j.UpdatedAt, now));
        if (closed == 0) return Result.Fail("This job is no longer waiting for review.");

        db.JobEvents.Add(new JobEvent
        {
            JobId = jobId, ActorId = actor.UserId, ActorName = actor.Name,
            Action = "closed", Description = $"Closed: \"{note.Trim()}\"",
        });
        await db.SaveChangesAsync();

        events.Publish(new DispatchChange(ChangeKind.Jobs, jobId));
        return Result.Ok();
    }

    public async Task<Result> AddNoteAsync(int jobId, string note, Actor actor)
    {
        note = note.Trim();
        if (note.Length == 0) return Result.Fail("The note is empty.");
        if (note.Length > 1000) note = note[..1000];

        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.Jobs.AnyAsync(j => j.Id == jobId)) return Result.Fail("Job not found.");

        db.JobEvents.Add(new JobEvent
        {
            JobId = jobId, ActorId = actor.UserId, ActorName = actor.Name,
            Action = "note", Description = $"Note: \"{note}\"",
        });
        await db.SaveChangesAsync();

        events.Publish(new DispatchChange(ChangeKind.Jobs, jobId));
        return Result.Ok();
    }

    /// <summary>Cancels a job only if no driver has it yet (used when a store cancels an order).</summary>
    public async Task<Result> CancelUnassignedJobAsync(int jobId, string reason, Actor actor)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var cancelled = await db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Unassigned)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled).SetProperty(j => j.UpdatedAt, now));
        if (cancelled == 0) return Result.Fail("This job is already with a driver or finished.");

        db.JobEvents.Add(new JobEvent
        {
            JobId = jobId, ActorId = actor.UserId, ActorName = actor.Name,
            Action = "cancelled", Description = $"Cancelled: {reason}",
        });
        await db.SaveChangesAsync();

        events.Publish(new DispatchChange(ChangeKind.Jobs, jobId));
        return Result.Ok();
    }

    // ---------- Helpers ----------

    private DateTimeOffset? ShiftToDate(DateTimeOffset? original, DateOnly date) =>
        original is { } t ? clock.ToUtc(date, TimeOnly.FromDateTime(clock.ToLocal(t).DateTime)) : null;

    private static async Task RefreshDriverStatusAsync(ApplicationDbContext db, int driverId)
    {
        var hasActive = await db.Jobs.AnyAsync(j =>
            j.DriverId == driverId && (j.Status == JobStatus.Assigned || j.Status == JobStatus.InProgress));

        await db.Drivers
            .Where(d => d.Id == driverId && d.Status != DriverStatus.Offline)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, hasActive ? DriverStatus.Busy : DriverStatus.Available));
    }

    private static Job BuildJob(NewJob input, Actor actor)
    {
        var now = DateTimeOffset.UtcNow;
        return new Job
        {
            Type = input.Type,
            ClientId = input.ClientId,
            RecipientName = Clean(input.RecipientName),
            RecipientPhone = Clean(input.RecipientPhone),
            ZoneId = input.ZoneId,
            ScheduledDate = input.ScheduledDate,
            WindowStart = input.WindowStart?.ToUniversalTime(),
            WindowEnd = input.WindowEnd?.ToUniversalTime(),
            Notes = Clean(input.Notes),
            Source = input.Source,
            ExternalId = Clean(input.ExternalId),
            CreatedById = actor.UserId,
            CreatedAt = now,
            UpdatedAt = now,
            Stops =
            {
                new Stop { Kind = StopKind.Pickup, Address = input.Pickup.Address, Lat = input.Pickup.Lat, Lng = input.Pickup.Lng },
                new Stop { Kind = StopKind.Dropoff, Address = input.Dropoff.Address, Lat = input.Dropoff.Lat, Lng = input.Dropoff.Lng },
            },
            Events = { new JobEvent { ActorId = actor.UserId, ActorName = actor.Name, Action = "created", Description = $"Created ({input.Source})" } },
        };
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
