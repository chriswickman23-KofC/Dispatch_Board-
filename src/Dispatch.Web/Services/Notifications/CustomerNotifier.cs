using Dispatch.Shared;
using Dispatch.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services.Notifications;

/// <summary>Sends customer texts: on the way, delivered, couldn't deliver, and rescheduled.</summary>
public sealed class CustomerNotifier(
    DispatchEvents events,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ITextSender sms,
    DispatchClock clock,
    IConfiguration config,
    ILogger<CustomerNotifier> log) : IHostedService
{
    private string Company => config["Branding:CompanyName"] ?? "Dispatch";
    private string BaseUrl => (config["App:PublicBaseUrl"]
        ?? throw new InvalidOperationException("App:PublicBaseUrl is not configured.")).TrimEnd('/');

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
        if (change.Kind != ChangeKind.Jobs || change.JobId is not int jobId) return;
        try { await CheckJobAsync(jobId); }
        catch (Exception ex) { log.LogWarning(ex, "Customer text check failed for job {JobId}", jobId); }
    }

    /// <summary>Delivered, failed, and rescheduled texts.</summary>
    public async Task CheckJobAsync(int jobId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.Jobs.AsNoTracking()
            .Include(j => j.Client)
            .Include(j => j.Stops)
            .Include(j => j.Events)
            .AsSplitQuery()
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;

        var from = FromText(job);

        switch (job.Status)
        {
            case JobStatus.Delivered:
            {
                var at = job.Stops.First(s => s.Kind == StopKind.Dropoff).CompletedAt ?? DateTimeOffset.UtcNow;
                await SendAsync(db, job, "delivered", "delivered",
                    link => $"{Company}: Your delivery{from} was delivered at {clock.ToLocal(at):h:mm tt}. Photo and details: {link}", ct);
                break;
            }

            case JobStatus.Failed when job.ReviewedAt is null:
            {
                var reason = job.Stops.Where(s => s.Status == StopStatus.Failed)
                    .OrderBy(s => s.CompletedAt).Select(s => s.FailureReason).FirstOrDefault();
                var why = CustomerMessages.FriendlyFailure(reason) is { } friendly ? $" because {friendly}" : "";
                await SendAsync(db, job, $"failed:{clock.Today:yyyy-MM-dd}", "couldn't deliver",
                    _ => $"{Company}: We couldn't complete your delivery{from} today{why}. We'll be in touch to reschedule.", ct);
                break;
            }

            case JobStatus.Unassigned when job.ScheduledDate is { } date
                                       && job.Events.OrderBy(e => e.CreatedAt).LastOrDefault()?.Action == "rescheduled":
            {
                var window = job.WindowStart is { } ws && job.WindowEnd is { } we
                    ? $" between {clock.ToLocal(ws):h:mm tt} and {clock.ToLocal(we):h:mm tt}" : "";
                await SendAsync(db, job, $"rescheduled:{date:yyyy-MM-dd}", "rescheduled",
                    _ => $"{Company}: Your delivery{from} has been rescheduled for {date:dddd, MMM d}{window}. We'll text you when it's on the way.", ct);
                break;
            }
        }
    }

    /// <summary>Sends "on the way" when a picked-up package's dropoff becomes the driver's next stop.</summary>
    public async Task CheckOnTheWayAsync(int driverId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var today = clock.Today;

            var routeId = await db.Routes
                .Where(r => r.DriverId == driverId && r.RouteDate == today)
                .Select(r => (int?)r.Id)
                .FirstOrDefaultAsync(ct);
            if (routeId is null) return;

            var next = await db.Stops.AsNoTracking()
                .Include(s => s.Job).ThenInclude(j => j.Client)
                .Where(s => s.RouteId == routeId && (s.Status == StopStatus.Pending || s.Status == StopStatus.Arrived))
                .OrderBy(s => s.Sequence)
                .FirstOrDefaultAsync(ct);
            if (next is null || next.Kind != StopKind.Dropoff || next.Job.Status != JobStatus.InProgress) return;

            var eta = next.EstimatedArrival is { } e ? $" Estimated arrival {CustomerMessages.Around(clock.ToLocal(e))}." : "";
            await SendAsync(db, next.Job, $"on_the_way:{today:yyyy-MM-dd}", "on the way",
                link => $"{Company}: Your delivery{FromText(next.Job)} is on the way.{eta} Track it: {link}", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "On-the-way check failed for driver {DriverId}", driverId);
        }
    }

    private async Task SendAsync(ApplicationDbContext db, Job job, string kind, string label,
        Func<string, string> buildBody, CancellationToken ct)
    {
        if (job.Client is { SendCustomerTexts: false }) return;
        if (PhoneNumbers.ToE164(job.RecipientPhone) is not { } to) return;
        if (await db.CustomerNotifications.AnyAsync(n => n.JobId == job.Id && n.Kind == kind, ct)) return;

        var isFirstText = !await db.CustomerNotifications.AnyAsync(n => n.JobId == job.Id && n.Status == NotificationStatus.Sent, ct);
        var token = await EnsureTrackingTokenAsync(db, job.Id, ct);
        var body = buildBody($"{BaseUrl}/track/{token}") + (isFirstText ? " Reply STOP to opt out." : "");

        // Claim the text first; the unique index stops a duplicate if two triggers race
        var record = new CustomerNotification { JobId = job.Id, Kind = kind, ToPhone = to, Body = body };
        db.CustomerNotifications.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return;   // someone else is already sending it
        }

        try
        {
            await sms.SendAsync(to, body, ct);
            record.Status = NotificationStatus.Sent;
            record.SentAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            record.Status = NotificationStatus.Failed;
            record.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            log.LogWarning(ex, "Customer text {Kind} failed for job {JobId}", kind, job.Id);
        }

        db.JobEvents.Add(new JobEvent
        {
            JobId = job.Id,
            ActorName = "System",
            Action = "customer_text",
            Description = $"Text to customer: {label} ({(record.Status == NotificationStatus.Sent ? "sent" : "failed")})",
        });
        await db.SaveChangesAsync(ct);
        events.Publish(new DispatchChange(ChangeKind.Etas));   // refresh open job panels without re-triggering texts
    }

    private static async Task<string> EnsureTrackingTokenAsync(ApplicationDbContext db, int jobId, CancellationToken ct)
    {
        var fresh = TrackingTokens.New();
        await db.Jobs.Where(j => j.Id == jobId && j.TrackingToken == null)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.TrackingToken, fresh), ct);
        return await db.Jobs.Where(j => j.Id == jobId).Select(j => j.TrackingToken!).FirstAsync(ct);
    }

    private static string FromText(Job job) => job.Client?.Name is { } name ? $" from {name}" : "";
}
