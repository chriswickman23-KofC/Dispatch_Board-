using Dispatch.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services;

/// <summary>Deletes driver location history older than 30 days, once a day.</summary>
public sealed class LocationPurgeService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<LocationPurgeService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
                var removed = await db.DriverLocations.Where(l => l.RecordedAt < cutoff).ExecuteDeleteAsync(ct);
                log.LogInformation("Purged {Count} location points older than 30 days", removed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Location purge failed");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
