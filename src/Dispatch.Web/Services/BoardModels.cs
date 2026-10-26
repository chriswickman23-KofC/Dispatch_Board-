using Dispatch.Shared;

namespace Dispatch.Web.Services;

public sealed record ZoneVm(int Id, string Name);

public sealed record StopVm(int Id, int JobId, StopKind Kind, string Address, int? RouteId, int? Sequence, StopStatus Status, DateTimeOffset? Eta);

public sealed record JobVm(
    int Id, string Ref, JobType Type, JobStatus Status, int? ZoneId,
    DateOnly? ScheduledDate, DateTimeOffset? WindowStart, DateTimeOffset? WindowEnd,
    DateTimeOffset CreatedAt, int? DriverId, string DisplayName, IReadOnlyList<StopVm> Stops)
{
    public StopVm? Pickup => Stops.FirstOrDefault(s => s.Kind == StopKind.Pickup);
    public StopVm? Dropoff => Stops.FirstOrDefault(s => s.Kind == StopKind.Dropoff);
}

public sealed record DriverVm(int Id, string Name, DriverStatus Status, int? ZoneId, DateTimeOffset? LastPingAt);

public sealed record RouteVm(int Id, int DriverId);

public sealed record RouteStopVm(StopVm Stop, JobVm Job);

public sealed record AttentionVm(
    int JobId, string Ref, int? ZoneId, string DisplayName, string? DriverName,
    StopKind FailedKind, string Address, string? Reason, DateTimeOffset? FailedAt);

public sealed record DeliveredVm(
    int JobId, string Ref, int? ZoneId, string DisplayName, string? DriverName,
    string Address, DateTimeOffset CompletedAt, bool HasPhoto, bool HasSignature, string? ReceivedBy);

public sealed record BoardSnapshot(
    IReadOnlyList<ZoneVm> Zones,
    IReadOnlyList<JobVm> Jobs,
    IReadOnlyList<DriverVm> Drivers,
    IReadOnlyList<RouteVm> Routes,
    IReadOnlyList<AttentionVm> Attention,
    IReadOnlyList<DeliveredVm> Delivered)
{
    public static BoardSnapshot Empty { get; } = new([], [], [], [], [], []);
}

public sealed record UserProfile(string FullName, int? ZoneId);
