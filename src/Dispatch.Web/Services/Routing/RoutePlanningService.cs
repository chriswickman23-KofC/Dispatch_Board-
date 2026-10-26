using Dispatch.Shared;
using Dispatch.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services.Routing;

public sealed record DriverOptimizeSummary(int Stops, DateTimeOffset? FinishEta, int CouldNotFit);

public sealed record PlanStop(int StopId, int JobId, string JobRef, StopKind Kind, string Address, DateTimeOffset Eta, bool IsNewJob, bool Late);

public sealed record PlanDriverRoute(int DriverId, string DriverName, IReadOnlyList<PlanStop> Stops,
    IReadOnlyList<int> NewJobIds, TimeSpan TotalTime, double DistanceMeters);

public sealed record PlanJob(int JobId, string Ref, string DisplayName, string DropoffAddress);

public sealed record PlanProposal(DateTimeOffset CreatedAt, IReadOnlyList<PlanDriverRoute> Routes, IReadOnlyList<PlanJob> Unplanned);

public sealed record PlanApplyResult(int Assigned, IReadOnlyList<string> Problems);

public sealed class RoutePlanningService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IRouteOptimizer optimizer,
    DispatchService dispatch,
    DispatchEvents events,
    DispatchClock clock,
    IConfiguration config)
{
    private sealed record DriverWork(Driver Driver, int? RouteId, List<Stop> OpenStops)
    {
        public Stop? Pinned => OpenStops.FirstOrDefault(s => s.Status == StopStatus.Arrived);
    }

    private TimeSpan PickupDuration => TimeSpan.FromMinutes(config.GetValue("Routing:PickupMinutes", 3));
    private TimeSpan DropoffDuration => TimeSpan.FromMinutes(config.GetValue("Routing:DropoffMinutes", 4));
    private int VehicleCapacity => config.GetValue("Routing:VehicleCapacity", 20);
    private TimeOnly DayEnds => TimeOnly.Parse(config["Routing:DayEndsAtLocal"] ?? "21:00");

    // ---------- One driver ----------

    public async Task<Result<DriverOptimizeSummary>> OptimizeDriverAsync(int driverId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var driver = await db.Drivers.AsNoTracking().FirstOrDefaultAsync(d => d.Id == driverId && d.Active);
        if (driver is null) return Result<DriverOptimizeSummary>.Fail("Driver not found.");
        if (driver.CurrentLat is null) return Result<DriverOptimizeSummary>.Fail($"No GPS position yet for {driver.Name}.");

        var work = (await LoadDriverWorkAsync(db, [driver])).Single();
        if (work.RouteId is null || work.OpenStops.Count < 2)
            return Result<DriverOptimizeSummary>.Fail("Nothing to optimize: fewer than two stops left.");

        var now = DateTimeOffset.UtcNow;
        var (problem, jobs) = BuildProblem([work], [], now);

        OptimizationResult result;
        try { result = await optimizer.OptimizeAsync(problem); }
        catch (RoutingException ex) { return Result<DriverOptimizeSummary>.Fail(ex.Message); }

        var planned = new List<(int StopId, DateTimeOffset Eta)>();
        if (work.Pinned is { } pinned) planned.Add((pinned.Id, now));
        foreach (var visit in result.Routes.FirstOrDefault()?.Visits ?? [])
            planned.Add((StopFor(jobs[visit.ShipmentId], visit.IsPickup).Id, visit.Arrival));

        var error = await ApplyOrderAsync(driverId, planned.Select(p => p.StopId).ToList(),
            planned.ToDictionary(p => p.StopId, p => p.Eta));
        if (error is not null) return Result<DriverOptimizeSummary>.Fail(error);

        var couldNotFit = work.OpenStops.Count - planned.Count;
        return Result<DriverOptimizeSummary>.Ok(new DriverOptimizeSummary(
            planned.Count, planned.Count > 0 ? planned.Max(p => p.Eta) : null, couldNotFit));
    }

    // ---------- Whole day ----------

    /// <summary>Builds a proposal without changing anything.</summary>
    public async Task<Result<PlanProposal>> ProposeDayPlanAsync(int? zoneId)
    {
        var today = clock.Today;
        await using var db = await dbFactory.CreateDbContextAsync();

        var drivers = await db.Drivers.AsNoTracking()
            .Where(d => d.Active && d.Status != DriverStatus.Offline && d.CurrentLat != null
                        && (zoneId == null || d.ZoneId == zoneId))
            .ToListAsync();
        if (drivers.Count == 0)
            return Result<PlanProposal>.Fail("No on-shift drivers with a GPS position in this zone.");

        var unassigned = await db.Jobs.AsNoTracking()
            .Include(j => j.Stops)
            .Include(j => j.Client)
            .Where(j => j.Status == JobStatus.Unassigned
                        && (j.ScheduledDate == null || j.ScheduledDate <= today)
                        && (zoneId == null || j.ZoneId == zoneId))
            .ToListAsync();
        if (unassigned.Count == 0)
            return Result<PlanProposal>.Fail("There are no unassigned jobs to plan.");

        var work = await LoadDriverWorkAsync(db, drivers);
        var now = DateTimeOffset.UtcNow;
        var (problem, jobs) = BuildProblem(work, unassigned, now);

        OptimizationResult result;
        try { result = await optimizer.OptimizeAsync(problem); }
        catch (RoutingException ex) { return Result<PlanProposal>.Fail(ex.Message); }

        var unassignedIds = unassigned.Select(j => j.Id).ToHashSet();
        var routes = new List<PlanDriverRoute>();

        foreach (var route in result.Routes)
        {
            var w = work.FirstOrDefault(x => VehicleId(x.Driver.Id) == route.VehicleId);
            if (w is null) continue;

            var stops = new List<PlanStop>();
            if (w.Pinned is { } pinned)
                stops.Add(new PlanStop(pinned.Id, pinned.JobId, pinned.Job.Ref, pinned.Kind, pinned.Address, now, false, false));

            foreach (var visit in route.Visits)
            {
                var job = jobs[visit.ShipmentId];
                var stop = StopFor(job, visit.IsPickup);
                var late = !visit.IsPickup && job.WindowEnd is { } end && visit.Arrival > end;
                stops.Add(new PlanStop(stop.Id, job.Id, job.Ref, stop.Kind, stop.Address, visit.Arrival, unassignedIds.Contains(job.Id), late));
            }

            var newJobs = route.Visits.Select(v => jobs[v.ShipmentId].Id).Where(unassignedIds.Contains).Distinct().ToList();
            routes.Add(new PlanDriverRoute(w.Driver.Id, w.Driver.Name, stops, newJobs, route.TotalTime, route.DistanceMeters));
        }

        var unplanned = unassigned
            .Where(j => result.Unassigned.Contains(ShipmentId(j.Id)))
            .Select(j => new PlanJob(j.Id, j.Ref, j.Client?.Name ?? j.RecipientName ?? "No name", StopFor(j, false).Address))
            .ToList();

        return Result<PlanProposal>.Ok(new PlanProposal(now, routes.OrderBy(r => r.DriverName).ToList(), unplanned));
    }

    /// <summary>Applies a proposal using the normal safe assignment, so jobs taken meanwhile are skipped.</summary>
    public async Task<PlanApplyResult> ApplyPlanAsync(PlanProposal plan, Actor actor)
    {
        var assigned = 0;
        var problems = new List<string>();

        foreach (var route in plan.Routes)
        {
            foreach (var jobId in route.NewJobIds)
            {
                var result = await dispatch.AssignAsync(jobId, route.DriverId, actor);
                if (result.Success) assigned++;
                else problems.Add($"{route.Stops.FirstOrDefault(s => s.JobId == jobId)?.JobRef ?? $"Job {jobId}"}: {result.Error}");
            }

            var error = await ApplyOrderAsync(route.DriverId, route.Stops.Select(s => s.StopId).ToList(),
                route.Stops.ToDictionary(s => s.StopId, s => s.Eta));
            if (error is not null) problems.Add($"{route.DriverName}: {error}");
        }

        return new PlanApplyResult(assigned, problems);
    }

    // ---------- Shared helpers ----------

    private async Task<List<DriverWork>> LoadDriverWorkAsync(ApplicationDbContext db, IReadOnlyList<Driver> drivers)
    {
        var today = clock.Today;
        var driverIds = drivers.Select(d => d.Id).ToList();

        var routes = await db.Routes.AsNoTracking()
            .Where(r => r.RouteDate == today && driverIds.Contains(r.DriverId))
            .ToListAsync();
        var routeIds = routes.Select(r => r.Id).ToList();

        var stops = await db.Stops.AsNoTracking()
            .Include(s => s.Job).ThenInclude(j => j.Stops)
            .Where(s => s.RouteId != null && routeIds.Contains(s.RouteId.Value)
                        && (s.Status == StopStatus.Pending || s.Status == StopStatus.Arrived))
            .ToListAsync();

        return drivers.Select(d =>
        {
            var route = routes.FirstOrDefault(r => r.DriverId == d.Id);
            var open = route is null ? new List<Stop>() : stops.Where(s => s.RouteId == route.Id).OrderBy(s => s.Sequence).ToList();
            return new DriverWork(d, route?.Id, open);
        }).ToList();
    }

    private (OptimizationProblem Problem, Dictionary<string, Job> Jobs) BuildProblem(
        IReadOnlyList<DriverWork> work, IReadOnlyList<Job> unassigned, DateTimeOffset now)
    {
        var endOfDay = clock.ToUtc(clock.Today, DayEnds);
        if (endOfDay < now.AddHours(1)) endOfDay = now.AddHours(4);   // late-evening planning still works

        var vehicles = new List<OptVehicle>();
        var shipments = new List<OptShipment>();
        var jobs = new Dictionary<string, Job>();

        foreach (var w in work)
        {
            var vehicleId = VehicleId(w.Driver.Id);
            var pinned = w.Pinned;   // a driver standing at a stop finishes it first
            var start = pinned is not null
                ? new OptPoint(pinned.Lat, pinned.Lng)
                : new OptPoint(w.Driver.CurrentLat!.Value, w.Driver.CurrentLng!.Value);
            vehicles.Add(new OptVehicle(vehicleId, start, now, endOfDay, VehicleCapacity));

            foreach (var job in w.OpenStops.Select(s => s.Job).DistinctBy(j => j.Id))
            {
                var pickup = StopFor(job, true);
                var dropoff = StopFor(job, false);
                if (pinned?.Id == dropoff.Id) continue;   // being delivered right now

                var onBoard = pickup.Status == StopStatus.Completed || pinned?.Id == pickup.Id;
                shipments.Add(MakeShipment(job, pickup, dropoff, onBoard, [vehicleId], now));   // stays with this driver
                jobs[ShipmentId(job.Id)] = job;
            }
        }

        foreach (var job in unassigned)
        {
            shipments.Add(MakeShipment(job, StopFor(job, true), StopFor(job, false), false, null, now));
            jobs[ShipmentId(job.Id)] = job;
        }

        return (new OptimizationProblem(vehicles, shipments, now), jobs);
    }

    private OptShipment MakeShipment(Job job, Stop pickup, Stop dropoff, bool onBoard,
        IReadOnlyList<string>? allowedVehicles, DateTimeOffset now)
    {
        // A window that has already closed is dropped so the job is still delivered as soon as possible
        OptWindow? window = job.WindowEnd is { } end && end > now
            ? new OptWindow(job.WindowStart is { } start && start > now ? start : now, end)
            : null;

        return new OptShipment(
            ShipmentId(job.Id),
            onBoard ? null : new OptTask($"s{pickup.Id}", new OptPoint(pickup.Lat, pickup.Lng), PickupDuration, null),
            new OptTask($"s{dropoff.Id}", new OptPoint(dropoff.Lat, dropoff.Lng), DropoffDuration, window),
            allowedVehicles);
    }

    /// <summary>Completed stops first, then the planned order, then anything that couldn't be planned.</summary>
    private async Task<string?> ApplyOrderAsync(int driverId, IReadOnlyList<int> plannedIds, IReadOnlyDictionary<int, DateTimeOffset> etas)
    {
        var today = clock.Today;
        await using var db = await dbFactory.CreateDbContextAsync();

        var routeId = await db.Routes
            .Where(r => r.DriverId == driverId && r.RouteDate == today)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync();
        if (routeId is null) return "no route for today";

        var current = await db.Stops.AsNoTracking()
            .Where(s => s.RouteId == routeId)
            .OrderBy(s => s.Sequence)
            .Select(s => new { s.Id, s.Status })
            .ToListAsync();
        var currentIds = current.Select(s => s.Id).ToHashSet();

        var order = current.Where(s => s.Status is StopStatus.Completed or StopStatus.Failed).Select(s => s.Id).ToList();
        order.AddRange(plannedIds.Where(id => currentIds.Contains(id) && !order.Contains(id)));
        order.AddRange(current.Select(s => s.Id).Where(id => !order.Contains(id)));

        var reorder = await dispatch.ReorderRouteAsync(routeId.Value, order);
        if (!reorder.Success) return reorder.Error;

        var ids = etas.Keys.Where(currentIds.Contains).ToList();
        var stops = await db.Stops.Where(s => ids.Contains(s.Id)).ToListAsync();
        foreach (var stop in stops) stop.EstimatedArrival = etas[stop.Id].ToUniversalTime();
        await db.SaveChangesAsync();

        events.Publish(new DispatchChange(ChangeKind.Etas, DriverId: driverId));
        return null;
    }

    private static Stop StopFor(Job job, bool pickup) =>
        job.Stops.First(s => s.Kind == (pickup ? StopKind.Pickup : StopKind.Dropoff));

    private static string ShipmentId(int jobId) => $"j{jobId}";
    private static string VehicleId(int driverId) => $"d{driverId}";
}
