using Dispatch.Shared;
using Dispatch.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services.Calendar;

public sealed record ClosedDayVm(int Id, DateOnly Date, string Name, string? CreatedBy, int ScheduledJobs);

public static class StandardHolidays
{
    public static IEnumerable<(DateOnly Date, string Name)> For(int year)
    {
        yield return (new DateOnly(year, 1, 1), "New Year's Day");
        yield return (LastWeekday(year, 5, DayOfWeek.Monday), "Memorial Day");
        yield return (new DateOnly(year, 7, 4), "Independence Day");
        yield return (NthWeekday(year, 9, DayOfWeek.Monday, 1), "Labor Day");
        yield return (NthWeekday(year, 11, DayOfWeek.Thursday, 4), "Thanksgiving Day");
        yield return (new DateOnly(year, 12, 25), "Christmas Day");
    }

    private static DateOnly NthWeekday(int year, int month, DayOfWeek day, int n)
    {
        var d = new DateOnly(year, month, 1);
        while (d.DayOfWeek != day) d = d.AddDays(1);
        return d.AddDays(7 * (n - 1));
    }

    private static DateOnly LastWeekday(int year, int month, DayOfWeek day)
    {
        var d = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        while (d.DayOfWeek != day) d = d.AddDays(-1);
        return d;
    }
}

public sealed class ClosedDayService(IDbContextFactory<ApplicationDbContext> dbFactory, DeliveryCalendar calendar, DispatchClock clock)
{
    public async Task<List<ClosedDayVm>> ListAsync(bool includePast)
    {
        var today = clock.Today;
        await using var db = await dbFactory.CreateDbContextAsync();

        var days = await db.ClosedDays.AsNoTracking()
            .Where(d => includePast || d.Date >= today)
            .OrderBy(d => d.Date)
            .ToListAsync();

        var dates = days.Select(d => d.Date).ToList();
        var counts = await db.Jobs.AsNoTracking()
            .Where(j => j.ScheduledDate != null && dates.Contains(j.ScheduledDate.Value)
                        && (j.Status == JobStatus.Unassigned || j.Status == JobStatus.Assigned || j.Status == JobStatus.InProgress))
            .GroupBy(j => j.ScheduledDate!.Value)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Date, x => x.Count);

        return days.Select(d => new ClosedDayVm(d.Id, d.Date, d.Name, d.CreatedBy, counts.GetValueOrDefault(d.Date))).ToList();
    }

    public async Task<bool> HasAnyInYearAsync(int year)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var start = new DateOnly(year, 1, 1);
        var end = new DateOnly(year + 1, 1, 1);
        return await db.ClosedDays.AnyAsync(d => d.Date >= start && d.Date < end);
    }

    public async Task<Result<string>> AddAsync(DateOnly date, string name, string actorName)
    {
        name = name.Trim();
        if (name.Length == 0) return Result<string>.Fail("Give the closed day a name, like \"Staff holiday\".");
        if (name.Length > 80) name = name[..80];

        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ClosedDays.FirstOrDefaultAsync(d => d.Date == date) is { } existing)
            return Result<string>.Fail($"{date:MMM d, yyyy} is already closed ({existing.Name}).");

        db.ClosedDays.Add(new ClosedDay { Date = date, Name = name, CreatedBy = actorName });
        await db.SaveChangesAsync();
        calendar.Invalidate();

        var affected = await db.Jobs.CountAsync(j => j.ScheduledDate == date
            && (j.Status == JobStatus.Unassigned || j.Status == JobStatus.Assigned || j.Status == JobStatus.InProgress));
        var message = $"{date:dddd, MMM d} is now closed ({name}).";
        if (affected > 0)
            message += $" Heads up: {affected} job{(affected == 1 ? " is" : "s are")} already scheduled that day. Reschedule them from the board.";
        return Result<string>.Ok(message);
    }

    public async Task RemoveAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ClosedDays.Where(d => d.Id == id).ExecuteDeleteAsync();
        calendar.Invalidate();
    }

    /// <summary>Adds the six major holidays for a year, skipping any already on the calendar.</summary>
    public async Task<int> AddStandardHolidaysAsync(int year, string actorName)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = (await db.ClosedDays.Select(d => d.Date).ToListAsync()).ToHashSet();

        var added = 0;
        foreach (var (date, name) in StandardHolidays.For(year))
        {
            if (!existing.Add(date)) continue;
            db.ClosedDays.Add(new ClosedDay { Date = date, Name = name, CreatedBy = actorName });
            added++;
        }
        await db.SaveChangesAsync();
        calendar.Invalidate();
        return added;
    }

    /// <summary>On first startup, preloads this year's and next year's holidays.</summary>
    public static async Task SeedIfEmptyAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        await using (var db = await sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync())
        {
            if (await db.ClosedDays.AnyAsync()) return;
        }

        var service = sp.GetRequiredService<ClosedDayService>();
        var year = sp.GetRequiredService<DispatchClock>().Today.Year;
        await service.AddStandardHolidaysAsync(year, "System");
        await service.AddStandardHolidaysAsync(year + 1, "System");
    }
}
