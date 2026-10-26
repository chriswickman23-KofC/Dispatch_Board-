using System.Threading.Channels;
using Dispatch.Shared;
using Dispatch.Web.Data;
using Dispatch.Web.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services.Eta;

/// <summary>Refreshes ETAs after route changes and every few minutes, then checks "on the way" texts.</summary>
public sealed class EtaRefresher(
    DispatchEvents events,
    IServiceScopeFactory scopes,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    CustomerNotifier notifier,
    IConfiguration config,
    ILogger<EtaRefresher> log) : BackgroundService
{
    private readonly Channel<int> _queue = Channel.CreateUnbounded<int>();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        events.Changed += OnChanged;
        try
        {
            var periodic = Task.Run(() => QueueBusyDriversPeriodicallyAsync(ct), ct);

            await foreach (var driverId in _queue.Reader.ReadAllAsync(ct))
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);   // let bursts settle
                var batch = new HashSet<int> { driverId };
                while (_queue.Reader.TryRead(out var more)) batch.Add(more);

                foreach (var id in batch) await RefreshAsync(id, ct);
            }
            await periodic;
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            events.Changed -= OnChanged;
        }
    }

    private Task OnChanged(DispatchChange change)
    {
        if (change.Kind == ChangeKind.Jobs && change.DriverId is int driverId)
            _queue.Writer.TryWrite(driverId);
        return Task.CompletedTask;
    }

    private async Task QueueBusyDriversPeriodicallyAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(config.GetValue("Eta:RefreshMinutes", 15)));
        while (await timer.WaitForNextTickAsync(ct))
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var busy = await db.Drivers
                .Where(d => d.Active && d.Status == DriverStatus.Busy)
                .Select(d => d.Id)
                .ToListAsync(ct);
            foreach (var id in busy) _queue.Writer.TryWrite(id);
        }
    }

    private async Task RefreshAsync(int driverId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<EtaService>().RefreshDriverAsync(driverId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "ETA refresh failed for driver {DriverId}", driverId);
        }

        // Runs even if the ETA lookup failed, so the text still goes out (just without a time)
        await notifier.CheckOnTheWayAsync(driverId, ct);
    }
}
