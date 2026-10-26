using System.Text.Json;
using Dispatch.Shared;
using Dispatch.Web.Data;
using Dispatch.Web.Services.Calendar;
using Dispatch.Web.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services.Intake;

public enum IntakeOutcome { Created, Duplicate, Skipped, Cancelled, Error }

public sealed record IntakeResult(
    IntakeOutcome Outcome, string? Detail = null, int? JobId = null, string? JobRef = null,
    string? TrackingToken = null, DateOnly? DeliveryDate = null);

/// <summary>The JSON accepted by the generic Order API.</summary>
public sealed record ApiOrderRequest(
    string? OrderId,
    ApiRecipient? Recipient,
    ApiAddress? Dropoff,
    ApiAddress? Pickup,
    DateOnly? DeliveryDate,
    ApiWindow? Window,
    bool Asap = false,
    string? Notes = null);

public sealed record ApiRecipient(string? Name, string? Phone);
public sealed record ApiAddress(string? Address, double? Lat, double? Lng);
public sealed record ApiWindow(string? Start, string? End);   // local times like "10:00"

public sealed class OrderIntakeService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    DispatchService dispatch,
    MapboxGeocoder geocoder,
    DeliveryCalendar calendar,
    DispatchClock clock,
    ILogger<OrderIntakeService> log)
{
    // ---------- Entry points ----------

    public async Task<IntakeResult> IntakeApiOrderAsync(ClientIntegration integration, ApiOrderRequest req)
    {
        var orderId = req.OrderId?.Trim();
        if (string.IsNullOrEmpty(orderId) || orderId.Length > 80)
            return await LogAsync(integration, "", new IntakeResult(IntakeOutcome.Error, "orderId is required (80 characters max)."));
        var externalId = $"api:{orderId}";

        if (string.IsNullOrWhiteSpace(req.Dropoff?.Address))
            return await LogAsync(integration, externalId, new IntakeResult(IntakeOutcome.Error, "dropoff.address is required."));

        TimeOnly? start = null, end = null;
        if (req.Window is not null)
        {
            if (!TimeOnly.TryParse(req.Window.Start, out var s) || !TimeOnly.TryParse(req.Window.End, out var e) || e <= s)
                return await LogAsync(integration, externalId, new IntakeResult(IntakeOutcome.Error, "window needs a start and end like \"10:00\", with end after start."));
            (start, end) = (s, e);
        }

        var order = new IncomingOrder(
            externalId, $"Order {orderId}",
            req.Recipient?.Name, req.Recipient?.Phone,
            req.Dropoff.Address.Trim(), req.Dropoff.Lat, req.Dropoff.Lng, req.Notes,
            req.Pickup?.Address?.Trim(), req.Pickup?.Lat, req.Pickup?.Lng,
            req.DeliveryDate, start, end, req.Asap);

        return await LogAsync(integration, externalId, await CreateAsync(integration, order));
    }

    public async Task HandleShopifyAsync(ClientIntegration integration, string topic, JsonElement payload)
    {
        var externalId = ShopifyOrders.ExternalId(payload);

        if (topic == "orders/cancelled")
        {
            await LogAsync(integration, externalId, await CancelAsync(integration, externalId));
            return;
        }
        if (topic is not ("orders/paid" or "orders/create"))
        {
            await LogAsync(integration, externalId, new IntakeResult(IntakeOutcome.Skipped, $"Ignored event \"{topic}\""));
            return;
        }

        var (order, skipReason) = ShopifyOrders.Map(payload, integration.Filter, integration.FilterValue);
        var result = order is null
            ? new IntakeResult(IntakeOutcome.Skipped, skipReason)
            : await CreateAsync(integration, order);
        await LogAsync(integration, externalId, result);
    }

    // ---------- Creating jobs ----------

    private async Task<IntakeResult> CreateAsync(ClientIntegration integration, IncomingOrder order)
    {
        if (await FindExistingAsync(integration, order.ExternalId) is { } existing) return existing;

        var client = integration.Client;
        var system = new Actor("system", $"{client.Name} (online order)");

        // Pickup: from the order if given, otherwise the client's default
        GeoPoint? pickup;
        if (order.PickupAddress is { Length: > 0 } pickupAddress)
        {
            pickup = (await LocateAsync(pickupAddress, order.PickupLat, order.PickupLng)).Point;
            if (pickup is null) return new IntakeResult(IntakeOutcome.Error, $"Couldn't find the pickup address: {pickupAddress}");
        }
        else if (client.DefaultPickupAddress is not null && client.DefaultPickupLat is double plat && client.DefaultPickupLng is double plng)
        {
            pickup = new GeoPoint(client.DefaultPickupAddress, plat, plng);
        }
        else
        {
            return new IntakeResult(IntakeOutcome.Error, $"{client.Name} has no default pickup address. Add one on the Clients page.");
        }

        var (dropoff, uncertain) = await LocateAsync(order.DropoffAddress, order.DropoffLat, order.DropoffLng);
        if (dropoff is null) return new IntakeResult(IntakeOutcome.Error, $"Couldn't find the delivery address: {order.DropoffAddress}");

        // Scheduling: ASAP, an explicit date, or the cutoff rule (which skips closed days)
        JobType type;
        DateOnly? date = null;
        DateTimeOffset? windowStart = null, windowEnd = null;
        string? closedNote = null;
        if (order.Asap)
        {
            type = JobType.OnDemand;
        }
        else
        {
            type = JobType.Scheduled;
            date = order.DeliveryDate is { } requested && requested >= clock.Today
                ? requested
                : await DeliveryDateForAsync(DateTimeOffset.UtcNow, integration.SameDayCutoff);

            // The store chose this date explicitly: keep it, but make sure dispatch notices
            if (order.DeliveryDate is not null && await calendar.ClosedReasonAsync(date.Value) is { } closedReason)
                closedNote = $"Requested for a closed day ({closedReason})";

            var start = order.WindowStart ?? integration.WindowStart;
            var end = order.WindowEnd ?? integration.WindowEnd;
            if (start is { } s && end is { } e && e > s)
            {
                windowStart = clock.ToUtc(date.Value, s);
                windowEnd = clock.ToUtc(date.Value, e);
            }
        }

        var notes = string.Join(" · ", new[]
        {
            order.DisplayRef,
            closedNote,
            uncertain ? "Address match uncertain, please verify" : null,
            order.Notes,
        }.Where(n => !string.IsNullOrWhiteSpace(n)));

        Result<Job> created;
        try
        {
            created = await dispatch.CreateJobAsync(new NewJob(
                type, pickup, dropoff,
                ClientId: client.Id,
                RecipientName: order.RecipientName,
                RecipientPhone: order.RecipientPhone,
                ZoneId: integration.ZoneId,
                ScheduledDate: date,
                WindowStart: windowStart,
                WindowEnd: windowEnd,
                Notes: notes,
                Source: JobSource.Api,
                ExternalId: order.ExternalId), system);
        }
        catch (DbUpdateException)
        {
            // The store sent the same order twice at the same moment; the unique index caught it
            return await FindExistingAsync(integration, order.ExternalId)
                   ?? new IntakeResult(IntakeOutcome.Error, "Couldn't save the order. It will be retried.");
        }
        if (!created.Success) return new IntakeResult(IntakeOutcome.Error, created.Error);

        var job = created.Value!;
        var token = TrackingTokens.New();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Jobs.Where(j => j.Id == job.Id && j.TrackingToken == null)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.TrackingToken, token));
        }

        var detail = string.Join("; ", new[] { closedNote, uncertain ? "Address match uncertain" : null }.Where(n => n is not null));
        return new IntakeResult(IntakeOutcome.Created, detail.Length > 0 ? detail : null, job.Id, job.Ref, token, date);
    }

    private async Task<IntakeResult> CancelAsync(ClientIntegration integration, string externalId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var job = await db.Jobs.AsNoTracking()
            .Where(j => j.ClientId == integration.ClientId && j.Source == JobSource.Api && j.ExternalId == externalId)
            .Select(j => new { j.Id, j.Ref, j.Status })
            .FirstOrDefaultAsync();
        if (job is null) return new IntakeResult(IntakeOutcome.Skipped, "No matching job (the order was never delivered here)");
        if (job.Status is JobStatus.Cancelled or JobStatus.Delivered)
            return new IntakeResult(IntakeOutcome.Skipped, $"Job is already {job.Status.ToString().ToLowerInvariant()}", job.Id, job.Ref);

        var system = new Actor("system", $"{integration.Client.Name} (online order)");
        var result = await dispatch.CancelUnassignedJobAsync(job.Id, "Order cancelled in the store", system);
        if (result.Success) return new IntakeResult(IntakeOutcome.Cancelled, null, job.Id, job.Ref);

        // Already with a driver: leave it for a dispatcher to handle, with a clear note
        await dispatch.AddNoteAsync(job.Id, "The store cancelled this order, but it's already with a driver. Check with the driver.", system);
        return new IntakeResult(IntakeOutcome.Cancelled, "Already with a driver; note added for dispatch", job.Id, job.Ref);
    }

    // ---------- Helpers ----------

    private async Task<IntakeResult?> FindExistingAsync(ClientIntegration integration, string externalId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.Jobs.AsNoTracking()
            .Where(j => j.ClientId == integration.ClientId && j.Source == JobSource.Api && j.ExternalId == externalId)
            .Select(j => new { j.Id, j.Ref, j.TrackingToken, j.ScheduledDate })
            .FirstOrDefaultAsync();
        return existing is null ? null
            : new IntakeResult(IntakeOutcome.Duplicate, "Already received", existing.Id, existing.Ref, existing.TrackingToken, existing.ScheduledDate);
    }

    private async Task<(GeoPoint? Point, bool Uncertain)> LocateAsync(string address, double? lat, double? lng)
    {
        if (lat is double la && lng is double lo && la is >= -90 and <= 90 && lo is >= -180 and <= 180)
            return (new GeoPoint(address, la, lo), false);   // the store already provided coordinates

        var hit = await geocoder.LocateAsync(address);
        return hit is null ? (null, false) : (new GeoPoint(hit.Address, hit.Lat, hit.Lng), hit.Confidence is "low" or "medium");
    }

    private async Task<DateOnly> DeliveryDateForAsync(DateTimeOffset receivedUtc, TimeOnly cutoff)
    {
        var local = clock.ToLocal(receivedUtc);
        var date = DateOnly.FromDateTime(local.DateTime);
        var sameDay = await calendar.IsOpenAsync(date) && TimeOnly.FromDateTime(local.DateTime) <= cutoff;
        return sameDay ? date : await calendar.NextOpenDayAsync(date);
    }

    private async Task<IntakeResult> LogAsync(ClientIntegration integration, string externalId, IntakeResult result)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.IntakeLogs.Add(new IntakeLog
            {
                IntegrationId = integration.Id,
                ExternalId = externalId.Length > 100 ? externalId[..100] : externalId,
                Outcome = result.Outcome.ToString(),
                Detail = result.Detail,
                JobId = result.JobId,
            });
            await db.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            var error = result.Outcome == IntakeOutcome.Error ? result.Detail : null;
            await db.ClientIntegrations.Where(i => i.Id == integration.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.LastOrderAt, now).SetProperty(i => i.LastError, error));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Couldn't write intake log for integration {Id}", integration.Id);
        }
        return result;
    }
}
