namespace Dispatch.Web.Services.Routing;

public sealed record OptPoint(double Lat, double Lng);

public sealed record OptWindow(DateTimeOffset Earliest, DateTimeOffset Latest);

public sealed record OptVehicle(string Id, OptPoint Start, DateTimeOffset AvailableFrom, DateTimeOffset AvailableUntil, int Capacity);

public sealed record OptTask(string Id, OptPoint Location, TimeSpan Duration, OptWindow? Window);

/// <summary>A pickup-and-dropoff pair, or dropoff only (Pickup is null) when the package is already on board.</summary>
public sealed record OptShipment(string Id, OptTask? Pickup, OptTask Dropoff, IReadOnlyList<string>? AllowedVehicles);

public sealed record OptimizationProblem(
    IReadOnlyList<OptVehicle> Vehicles,
    IReadOnlyList<OptShipment> Shipments,
    DateTimeOffset PlanStart);

public sealed record PlannedVisit(string ShipmentId, bool IsPickup, DateTimeOffset Arrival);

public sealed record PlannedRoute(string VehicleId, IReadOnlyList<PlannedVisit> Visits, TimeSpan TotalTime, double DistanceMeters);

public sealed record OptimizationResult(IReadOnlyList<PlannedRoute> Routes, IReadOnlyList<string> Unassigned);

public interface IRouteOptimizer
{
    Task<OptimizationResult> OptimizeAsync(OptimizationProblem problem, CancellationToken ct = default);
}

public sealed class RoutingException(string message) : Exception(message);
