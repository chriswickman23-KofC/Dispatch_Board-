using System.Globalization;
using System.Text.Json;

namespace Dispatch.Web.Services.Eta;

public sealed class MapboxDirections(HttpClient http, IConfiguration config, ILogger<MapboxDirections> log)
{
    public const int MaxPoints = 25;   // Mapbox's waypoint limit per request

    private string Token => config["Mapbox:Token"]
        ?? throw new InvalidOperationException("Mapbox:Token is not configured.");

    /// <summary>Drive time between each consecutive pair of points, or null if the lookup failed.</summary>
    public async Task<IReadOnlyList<TimeSpan>?> GetLegDurationsAsync(IReadOnlyList<(double Lat, double Lng)> points, CancellationToken ct)
    {
        if (points.Count < 2) return [];
        if (points.Count > MaxPoints) points = points.Take(MaxPoints).ToList();

        var coords = string.Join(";", points.Select(p =>
            $"{p.Lng.ToString(CultureInfo.InvariantCulture)},{p.Lat.ToString(CultureInfo.InvariantCulture)}"));

        using var res = await http.GetAsync($"directions/v5/mapbox/driving/{coords}?overview=false&steps=false&access_token={Token}", ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Mapbox Directions returned {Status}", res.StatusCode);
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0) return null;

        return routes[0].GetProperty("legs").EnumerateArray()
            .Select(leg => TimeSpan.FromSeconds(leg.GetProperty("duration").GetDouble()))
            .ToList();
    }
}
