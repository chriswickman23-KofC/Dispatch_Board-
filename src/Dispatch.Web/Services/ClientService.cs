using System.Net.Mail;
using System.Text.Json;
using Dispatch.Shared;
using Dispatch.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services;

public sealed record ClientVm(int Id, string Name, GeoPoint? DefaultPickup, string? CsvMapping);

public sealed record ClientStats(
    int Delivered, int FailedAttempts, int WithWindow, int OnTime,
    double? AvgOnDemandMinutes, int OpenNow, DateTimeOffset? LastDeliveredAt)
{
    public double? OnTimeRate => WithWindow == 0 ? null : (double)OnTime / WithWindow;
    public double? FailureRate => Delivered + FailedAttempts == 0 ? null : (double)FailedAttempts / (Delivered + FailedAttempts);
    public static ClientStats Empty { get; } = new(0, 0, 0, 0, null, 0, null);
}

public sealed record ClientRowVm(
    int Id, string Name, string? ContactName, string? ContactPhone, string? ContactEmail,
    string? DefaultPickupAddress, bool SendCustomerTexts, bool Active, bool HasCsvLayout, ClientStats Stats);

public sealed record FailureReasonCount(string Reason, int Count);

public sealed class ClientForm
{
    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }
    public GeoPoint? DefaultPickup { get; set; }
    public bool SendCustomerTexts { get; set; } = true;
    public string? Notes { get; set; }
    public bool Active { get; set; } = true;
    public int CsvColumnCount { get; set; }
}

public sealed class ClientService(IDbContextFactory<ApplicationDbContext> dbFactory, DispatchClock clock)
{
    // ---------- Used by the job form and CSV importer ----------

    public async Task<List<ClientVm>> GetActiveAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Clients.AsNoTracking()
            .Where(c => c.Active)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.DefaultPickupAddress, c.DefaultPickupLat, c.DefaultPickupLng, c.CsvMapping })
            .ToListAsync();

        return rows.Select(c => new ClientVm(
            c.Id, c.Name,
            c.DefaultPickupAddress is not null && c.DefaultPickupLat is double lat && c.DefaultPickupLng is double lng
                ? new GeoPoint(c.DefaultPickupAddress, lat, lng) : null,
            c.CsvMapping)).ToList();
    }

    public async Task SaveCsvMappingAsync(int clientId, string mappingJson)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Clients.Where(c => c.Id == clientId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.CsvMapping, mappingJson));
    }

    public async Task ResetCsvMappingAsync(int clientId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Clients.Where(c => c.Id == clientId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.CsvMapping, (string?)null));
    }

    // ---------- Clients page ----------

    public async Task<List<ClientRowVm>> GetAllWithStatsAsync(int days)
    {
        var since = PeriodStart(days);
        await using var db = await dbFactory.CreateDbContextAsync();

        var clients = await db.Clients.AsNoTracking().OrderBy(c => c.Name).ToListAsync();

        var delivered = (await db.Stops.AsNoTracking()
                .Where(s => s.Kind == StopKind.Dropoff && s.Status == StopStatus.Completed
                            && s.CompletedAt >= since && s.Job.ClientId != null)
                .Select(s => new
                {
                    ClientId = s.Job.ClientId!.Value,
                    CompletedAt = s.CompletedAt!.Value,
                    s.Job.WindowEnd,
                    s.Job.Type,
                    s.Job.CreatedAt,
                })
                .ToListAsync())
            .ToLookup(r => r.ClientId);

        // Failed attempts come from history, so rescheduled jobs still count
        var failures = (await (
                from e in db.JobEvents
                join j in db.Jobs on e.JobId equals j.Id
                where e.Action == "stop_failed" && e.CreatedAt >= since && j.ClientId != null
                select j.ClientId!.Value)
                .ToListAsync())
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var open = await db.Jobs.AsNoTracking()
            .Where(j => j.ClientId != null
                        && (j.Status == JobStatus.Unassigned || j.Status == JobStatus.Assigned || j.Status == JobStatus.InProgress))
            .GroupBy(j => j.ClientId!.Value)
            .Select(g => new { ClientId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ClientId, x => x.Count);

        var lastDelivered = await db.Stops.AsNoTracking()
            .Where(s => s.Kind == StopKind.Dropoff && s.Status == StopStatus.Completed && s.Job.ClientId != null)
            .Select(s => new { ClientId = s.Job.ClientId!.Value, s.CompletedAt })
            .GroupBy(x => x.ClientId)
            .Select(g => new { ClientId = g.Key, Last = g.Max(x => x.CompletedAt) })
            .ToDictionaryAsync(x => x.ClientId, x => x.Last);

        return clients.Select(c =>
        {
            var rows = delivered[c.Id].ToList();
            var windowed = rows.Where(r => r.WindowEnd is not null).ToList();
            var onDemandMinutes = rows
                .Where(r => r.Type == JobType.OnDemand)
                .Select(r => (r.CompletedAt - r.CreatedAt).TotalMinutes)
                .ToList();

            var stats = new ClientStats(
                Delivered: rows.Count,
                FailedAttempts: failures.GetValueOrDefault(c.Id),
                WithWindow: windowed.Count,
                OnTime: windowed.Count(r => r.CompletedAt <= r.WindowEnd),
                AvgOnDemandMinutes: onDemandMinutes.Count > 0 ? onDemandMinutes.Average() : null,
                OpenNow: open.GetValueOrDefault(c.Id),
                LastDeliveredAt: lastDelivered.GetValueOrDefault(c.Id));

            return new ClientRowVm(
                c.Id, c.Name, c.ContactName, c.ContactPhone, c.ContactEmail,
                c.DefaultPickupAddress, c.SendCustomerTexts, c.Active, c.CsvMapping is not null, stats);
        }).ToList();
    }

    public async Task<List<FailureReasonCount>> GetFailureReasonsAsync(int clientId, int days)
    {
        var since = PeriodStart(days);
        await using var db = await dbFactory.CreateDbContextAsync();

        var descriptions = await (
            from e in db.JobEvents
            join j in db.Jobs on e.JobId equals j.Id
            where e.Action == "stop_failed" && e.CreatedAt >= since && j.ClientId == clientId
            select e.Description)
            .ToListAsync();

        // Descriptions look like "Delivery failed: Nobody available"
        return descriptions
            .Select(d => d.IndexOf("failed: ", StringComparison.Ordinal) is var i and >= 0 ? d[(i + 8)..] : "Other")
            .GroupBy(reason => reason)
            .Select(g => new FailureReasonCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();
    }

    public async Task<ClientForm?> GetFormAsync(int clientId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = await db.Clients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == clientId);
        if (c is null) return null;

        return new ClientForm
        {
            Id = c.Id,
            Name = c.Name,
            ContactName = c.ContactName,
            ContactPhone = c.ContactPhone,
            ContactEmail = c.ContactEmail,
            DefaultPickup = c.DefaultPickupAddress is not null && c.DefaultPickupLat is double lat && c.DefaultPickupLng is double lng
                ? new GeoPoint(c.DefaultPickupAddress, lat, lng) : null,
            SendCustomerTexts = c.SendCustomerTexts,
            Notes = c.Notes,
            Active = c.Active,
            CsvColumnCount = CountColumns(c.CsvMapping),
        };
    }

    public async Task<Result<string>> SaveAsync(ClientForm form)
    {
        var name = form.Name.Trim();
        if (name.Length == 0) return Result<string>.Fail("Enter the client's name.");
        if (!string.IsNullOrWhiteSpace(form.ContactEmail) && !MailAddress.TryCreate(form.ContactEmail.Trim(), out _))
            return Result<string>.Fail("The contact email doesn't look right.");
        if (!string.IsNullOrWhiteSpace(form.ContactPhone) && form.ContactPhone.Count(char.IsDigit) < 10)
            return Result<string>.Fail("The contact phone number looks incomplete.");

        await using var db = await dbFactory.CreateDbContextAsync();

        var selfId = form.Id ?? 0;
        if (await db.Clients.AnyAsync(c => c.Id != selfId && c.Name.ToLower() == name.ToLower()))
            return Result<string>.Fail($"There's already a client called \"{name}\".");

        Client? client;
        if (form.Id is int id)
        {
            client = await db.Clients.FindAsync(id);
            if (client is null) return Result<string>.Fail("Client not found.");
        }
        else
        {
            client = new Client();
            db.Clients.Add(client);
        }

        client.Name = name;
        client.ContactName = Clean(form.ContactName);
        client.ContactPhone = Clean(form.ContactPhone);
        client.ContactEmail = Clean(form.ContactEmail);
        client.DefaultPickupAddress = form.DefaultPickup?.Address;
        client.DefaultPickupLat = form.DefaultPickup?.Lat;
        client.DefaultPickupLng = form.DefaultPickup?.Lng;
        client.SendCustomerTexts = form.SendCustomerTexts;
        client.Notes = Clean(form.Notes);
        client.Active = form.Active;

        await db.SaveChangesAsync();
        return Result<string>.Ok(form.Id is null ? $"{name} added." : $"{name} saved.");
    }

    // ---------- Helpers ----------

    /// <summary>"Last 30 days" = today plus the 29 days before it, in Arlington time.</summary>
    private DateTimeOffset PeriodStart(int days) => clock.StartOfTodayUtc.AddDays(-(Math.Max(days, 1) - 1));

    private static int CountColumns(string? mappingJson)
    {
        if (string.IsNullOrEmpty(mappingJson)) return 0;
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(mappingJson)?.Count ?? 0; }
        catch (JsonException) { return 0; }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
