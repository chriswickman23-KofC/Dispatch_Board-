using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dispatch.Web.Services.Routing;

public sealed class GraphHopperOptimizer(HttpClient http, IConfiguration config, ILogger<GraphHopperOptimizer> log) : IRouteOptimizer
{
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,   // GraphHopper rejects explicit nulls
    };

    private string Key => config["Routing:GraphHopperKey"]
        ?? throw new InvalidOperationException("Routing:GraphHopperKey is not configured.");

    public async Task<OptimizationResult> OptimizeAsync(OptimizationProblem problem, CancellationToken ct = default)
    {
        // All times are sent as seconds after PlanStart
        long Seconds(DateTimeOffset t) => Math.Max(0, (long)(t - problem.PlanStart).TotalSeconds);
        object Address(string id, OptPoint p) => new { location_id = id, lon = p.Lng, lat = p.Lat };
        object[]? Windows(OptWindow? w) => w is null ? null : [new { earliest = Seconds(w.Earliest), latest = Seconds(w.Latest) }];

        var capacity = problem.Vehicles.Count == 0 ? 20 : problem.Vehicles.Max(v => v.Capacity);

        var request = new
        {
            vehicles = problem.Vehicles.Select(v => new
            {
                vehicle_id = v.Id,
                type_id = "car",
                start_address = Address($"{v.Id}-start", v.Start),
                return_to_depot = false,
                earliest_start = Seconds(v.AvailableFrom),
                latest_end = Seconds(v.AvailableUntil),
            }),
            vehicle_types = new[] { new { type_id = "car", profile = "car", capacity = new[] { capacity } } },
            shipments = problem.Shipments.Where(s => s.Pickup is not null).Select(s => new
            {
                id = s.Id,
                size = new[] { 1 },
                allowed_vehicles = s.AllowedVehicles,
                pickup = new
                {
                    address = Address(s.Pickup!.Id, s.Pickup.Location),
                    duration = (long)s.Pickup.Duration.TotalSeconds,
                    time_windows = Windows(s.Pickup.Window),
                },
                delivery = new
                {
                    address = Address(s.Dropoff.Id, s.Dropoff.Location),
                    duration = (long)s.Dropoff.Duration.TotalSeconds,
                    time_windows = Windows(s.Dropoff.Window),
                },
            }),
            // "delivery" services start on board the vehicle: packages the driver already picked up
            services = problem.Shipments.Where(s => s.Pickup is null).Select(s => new
            {
                id = s.Id,
                type = "delivery",
                size = new[] { 1 },
                allowed_vehicles = s.AllowedVehicles,
                address = Address(s.Dropoff.Id, s.Dropoff.Location),
                duration = (long)s.Dropoff.Duration.TotalSeconds,
                time_windows = Windows(s.Dropoff.Window),
            }),
            configuration = new { routing = new { calc_points = false } },
        };

        using var res = await http.PostAsJsonAsync($"vrp?key={Key}", request, RequestJson, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("GraphHopper returned {Status}: {Body}", res.StatusCode, body);
            throw new RoutingException(ExtractMessage(body) ?? $"Route optimization failed ({(int)res.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(body);
        var solution = doc.RootElement.GetProperty("solution");

        var routes = new List<PlannedRoute>();
        foreach (var route in solution.GetProperty("routes").EnumerateArray())
        {
            var visits = new List<PlannedVisit>();
            foreach (var activity in route.GetProperty("activities").EnumerateArray())
            {
                var type = activity.GetProperty("type").GetString();
                if (type is "start" or "end") continue;

                var arrival = activity.TryGetProperty("arr_time", out var arr) ? arr.GetInt64() : 0;
                visits.Add(new PlannedVisit(
                    activity.GetProperty("id").GetString()!,
                    IsPickup: type == "pickupShipment",
                    problem.PlanStart.AddSeconds(arrival)));
            }

            var seconds = route.TryGetProperty("completion_time", out var completion) ? completion.GetInt64()
                        : route.TryGetProperty("transport_time", out var transport) ? transport.GetInt64()
                        : 0;
            var distance = route.TryGetProperty("distance", out var dist) ? dist.GetDouble() : 0;

            routes.Add(new PlannedRoute(route.GetProperty("vehicle_id").GetString()!, visits, TimeSpan.FromSeconds(seconds), distance));
        }

        var unassigned = new List<string>();
        if (solution.TryGetProperty("unassigned", out var un))
        {
            foreach (var key in new[] { "shipments", "services" })
                if (un.TryGetProperty(key, out var ids))
                    unassigned.AddRange(ids.EnumerateArray().Select(x => x.GetString()!));
        }

        return new OptimizationResult(routes, unassigned);
    }

    private static string? ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
