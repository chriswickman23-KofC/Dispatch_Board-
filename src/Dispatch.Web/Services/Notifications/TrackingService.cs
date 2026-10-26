using Dispatch.Shared;
using Dispatch.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services.Notifications;

public enum TrackingStage { Scheduled, WithDriver, OnTheWay, Delivered, NotDelivered, Expired }

public sealed record GeoPos(double Lat, double Lng);

public sealed record TrackingVm(
    int JobId, string Company, string? From, TrackingStage Stage,
    DateTimeOffset? Eta, DateOnly? ScheduledDate, DateTimeOffset? WindowStart, DateTimeOffset? WindowEnd,
    int? DriverId, string? DriverFirstName, GeoPos Destination, GeoPos? DriverPosition, int StopsBefore,
    DateTimeOffset? DeliveredAt, bool HasPhoto, string? ReceivedBy, string? Problem);

public sealed class TrackingService(IDbContextFactory<ApplicationDbContext> dbFactory, IConfiguration config)
{
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(7);

    public async Task<TrackingVm?> LoadAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;

        await using var db = await dbFactory.CreateDbContextAsync();
        var job = await db.Jobs.AsNoTracking()
            .Include(j => j.Client)
            .Include(j => j.Driver)
            .Include(j => j.Stops).ThenInclude(s => s.Proofs)
            .AsSplitQuery()
            .FirstOrDefaultAsync(j => j.TrackingToken == token);
        if (job is null) return null;

        var dropoff = job.Stops.First(s => s.Kind == StopKind.Dropoff);
        var finishedAt = job.Stops.Where(s => s.CompletedAt != null).Max(s => s.CompletedAt);
        var stopsBefore = 0;
        GeoPos? driverPosition = null;

        TrackingStage stage;
        if (job.Status is JobStatus.Delivered or JobStatus.Failed && finishedAt is { } f && DateTimeOffset.UtcNow - f > LinkLifetime)
        {
            stage = TrackingStage.Expired;
        }
        else
        {
            switch (job.Status)
            {
                case JobStatus.Delivered:
                    stage = TrackingStage.Delivered;
                    break;
                case JobStatus.Failed or JobStatus.Cancelled:
                    stage = TrackingStage.NotDelivered;
                    break;
                case JobStatus.Assigned or JobStatus.InProgress when dropoff.RouteId is not null:
                    stopsBefore = await db.Stops.CountAsync(s =>
                        s.RouteId == dropoff.RouteId && s.Sequence < dropoff.Sequence
                        && (s.Status == StopStatus.Pending || s.Status == StopStatus.Arrived));
                    stage = job.Status == JobStatus.InProgress && stopsBefore == 0 ? TrackingStage.OnTheWay : TrackingStage.WithDriver;
                    // Only reveal the driver's position while they're heading to this customer
                    if (stage == TrackingStage.OnTheWay && job.Driver is { CurrentLat: double lat, CurrentLng: double lng })
                        driverPosition = new GeoPos(lat, lng);
                    break;
                default:
                    stage = TrackingStage.Scheduled;
                    break;
            }
        }

        var proof = dropoff.Proofs.OrderBy(p => p.CapturedAt).FirstOrDefault();
        var failure = job.Stops.Where(s => s.Status == StopStatus.Failed).OrderBy(s => s.CompletedAt).Select(s => s.FailureReason).FirstOrDefault();

        return new TrackingVm(
            job.Id,
            config["Branding:CompanyName"] ?? "Dispatch",
            job.Client?.Name,
            stage,
            dropoff.EstimatedArrival,
            job.ScheduledDate,
            job.WindowStart,
            job.WindowEnd,
            job.DriverId,
            job.Driver?.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
            new GeoPos(dropoff.Lat, dropoff.Lng),
            driverPosition,
            stopsBefore,
            stage == TrackingStage.Delivered ? dropoff.CompletedAt : null,
            stage == TrackingStage.Delivered && proof?.PhotoPath is not null,
            stage == TrackingStage.Delivered ? proof?.RecipientName : null,
            stage == TrackingStage.NotDelivered ? CustomerMessages.FriendlyFailure(failure) : null);
    }

    /// <summary>The delivery photo path, only for a delivered job whose link hasn't expired.</summary>
    public async Task<string?> GetPhotoPathAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        var cutoff = DateTimeOffset.UtcNow - LinkLifetime;

        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Stops.AsNoTracking()
            .Where(s => s.Job.TrackingToken == token && s.Job.Status == JobStatus.Delivered
                        && s.Kind == StopKind.Dropoff && s.CompletedAt > cutoff)
            .SelectMany(s => s.Proofs)
            .Where(p => p.PhotoPath != null)
            .OrderBy(p => p.CapturedAt)
            .Select(p => p.PhotoPath)
            .FirstOrDefaultAsync();
    }
}
