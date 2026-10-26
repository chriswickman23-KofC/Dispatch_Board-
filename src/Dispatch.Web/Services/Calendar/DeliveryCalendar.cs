using Dispatch.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services.Calendar;

/// <summary>The one place that decides whether a day is open: weekly delivery days plus closed days.</summary>
public sealed class DeliveryCalendar(IDbContextFactory<ApplicationDbContext> dbFactory, IConfiguration config)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<DateOnly, string> _closed = [];
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public IReadOnlySet<DayOfWeek> WeeklyDays =>
        (config.GetSection("Intake:DeliveryDays").Get<string[]>()
         ?? ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"])
        .Select(Enum.Parse<DayOfWeek>)
        .ToHashSet();

    /// <summary>Why a day is closed ("Thanksgiving Day", "Sundays are not delivery days"), or null if it's open.</summary>
    public async Task<string?> ClosedReasonAsync(DateOnly date)
    {
        await EnsureLoadedAsync();
        if (_closed.TryGetValue(date, out var name)) return name;
        return WeeklyDays.Contains(date.DayOfWeek) ? null : $"{date.DayOfWeek}s are not delivery days";
    }

    public async Task<bool> IsOpenAsync(DateOnly date) => await ClosedReasonAsync(date) is null;

    /// <summary>The first open day after the given date.</summary>
    public async Task<DateOnly> NextOpenDayAsync(DateOnly after)
    {
        for (var i = 1; i <= 60; i++)
        {
            var candidate = after.AddDays(i);
            if (await IsOpenAsync(candidate)) return candidate;
        }
        return after.AddDays(1);   // misconfigured calendar (everything closed): fail safe rather than loop forever
    }

    /// <summary>Called after closed days change, so the next check sees them right away.</summary>
    public void Invalidate() => _loadedAt = DateTimeOffset.MinValue;

    private async Task EnsureLoadedAsync()
    {
        if (DateTimeOffset.UtcNow - _loadedAt < CacheFor) return;
        await _gate.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow - _loadedAt < CacheFor) return;   // another caller just loaded it
            await using var db = await dbFactory.CreateDbContextAsync();
            _closed = await db.ClosedDays.AsNoTracking().ToDictionaryAsync(d => d.Date, d => d.Name);
            _loadedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}
