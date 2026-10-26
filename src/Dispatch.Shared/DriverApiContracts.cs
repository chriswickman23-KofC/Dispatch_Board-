namespace Dispatch.Shared;

public sealed record DriverLoginRequest(string Email, string Password);

/// <summary>Matches the token response written by ASP.NET Core's bearer token handler.</summary>
public sealed record TokenResponse(string TokenType, string AccessToken, long ExpiresIn, string RefreshToken);

public sealed record TokenRefreshRequest(string RefreshToken);

public sealed record DriverProfileDto(int Id, string Name, DriverStatus Status);

public sealed record RouteStopDto(
    int Id,
    StopKind Kind,
    string Address,
    double Lat,
    double Lng,
    int Sequence,
    StopStatus Status,
    int JobId,
    string JobRef,
    JobType JobType,
    JobStatus JobStatus,
    string DisplayName,
    string? RecipientName,
    string? RecipientPhone,
    string? Notes,
    DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd,
    bool PickupDone);

public sealed record LocationDto(double Lat, double Lng, float? Accuracy, DateTimeOffset RecordedAt);

public sealed record StopStatusRequest(StopStatus Status, string? Reason);

public sealed record LocationsAccepted(int Accepted);

public static class FailureReasons
{
    public static readonly string[] All =
    [
        "Nobody available",
        "Wrong or missing address",
        "Recipient refused",
        "Couldn't access building",
        "Item damaged",
        "Other",
    ];
}

public static class DriverHubEvents
{
    public const string RouteChanged = "RouteChanged";
}
