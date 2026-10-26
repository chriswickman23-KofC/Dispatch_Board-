namespace Dispatch.Web.Services;

public sealed record Result(bool Success, string? Error = null)
{
    public static Result Ok() => new(true);
    public static Result Fail(string error) => new(false, error);
}

public sealed record Result<T>(bool Success, T? Value, string? Error = null)
{
    public static Result<T> Ok(T value) => new(true, value);
    public static Result<T> Fail(string error) => new(false, default, error);
}

/// <summary>Who is acting. DriverId is set when the caller is the driver app.</summary>
public sealed record Actor(string UserId, string Name, int? DriverId = null);

public sealed record GeoPoint(string Address, double Lat, double Lng);

/// <summary>Business dates and times follow Arlington time, not the server's UTC clock.</summary>
public sealed class DispatchClock
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public DateOnly Today => DateOnly.FromDateTime(LocalNow.DateTime);
    public DateTimeOffset LocalNow => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Tz);
    public DateTimeOffset ToLocal(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Tz);

    public DateTimeOffset StartOfTodayUtc
    {
        get
        {
            var local = LocalNow;
            return new DateTimeOffset(local.Date, local.Offset).ToUniversalTime();
        }
    }

    /// <summary>Converts a date and time entered in Arlington time into UTC for storage.</summary>
    public DateTimeOffset ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time);
        return new DateTimeOffset(local, Tz.GetUtcOffset(local)).ToUniversalTime();
    }
}
