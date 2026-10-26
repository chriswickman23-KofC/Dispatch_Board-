using System.Net.Http.Json;
using System.Text.Json;

namespace Dispatch.Web.Services;

public sealed record GeoSuggestion(string Address, double Lat, double Lng, string? Confidence);

public sealed class MapboxGeocoder(HttpClient http, IConfiguration config, ILogger<MapboxGeocoder> log)
{
    // Bias toward Arlington while still allowing DC, Alexandria, and nearby Fairfax
    private const string Proximity = "-77.1,38.88";
    private const string Bbox = "-77.5,38.6,-76.8,39.1";

    private string Token => config["Mapbox:Token"]
        ?? throw new InvalidOperationException("Mapbox:Token is not configured.");

    /// <summary>Autocomplete suggestions (temporary geocoding, free tier).</summary>
    public Task<List<GeoSuggestion>> SuggestAsync(string query, CancellationToken ct = default)
    {
        if (query.Trim().Length < 3) return Task.FromResult(new List<GeoSuggestion>());
        return GetAsync(BuildUrl(query, limit: 5, autocomplete: true, permanent: false), ct);
    }

    /// <summary>One permanent lookup for coordinates we'll store.</summary>
    public async Task<GeoSuggestion?> LocateAsync(string address, CancellationToken ct = default) =>
        (await GetAsync(BuildUrl(address, limit: 1, autocomplete: false, permanent: true), ct)).FirstOrDefault();

    /// <summary>Permanent lookups for many addresses in one request (CSV import).</summary>
    public async Task<List<GeoSuggestion?>> LocateBatchAsync(IReadOnlyList<string> addresses, CancellationToken ct = default)
    {
        var body = addresses.Select(q => new
        {
            q,
            types = new[] { "address" },
            country = "us",
            proximity = new[] { -77.1, 38.88 },
            bbox = new[] { -77.5, 38.6, -76.8, 39.1 },
            limit = 1,
        });

        using var res = await http.PostAsJsonAsync($"search/geocode/v6/batch?access_token={Token}&permanent=true", body, ct);
        res.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var results = new List<GeoSuggestion?>();
        foreach (var collection in doc.RootElement.GetProperty("batch").EnumerateArray())
            results.Add(ParseFeatures(collection).FirstOrDefault());
        return results;
    }

    private string BuildUrl(string query, int limit, bool autocomplete, bool permanent) =>
        $"search/geocode/v6/forward?q={Uri.EscapeDataString(query)}&access_token={Token}" +
        $"&country=us&types=address&proximity={Proximity}&bbox={Bbox}&limit={limit}" +
        $"&autocomplete={(autocomplete ? "true" : "false")}" + (permanent ? "&permanent=true" : "");

    private async Task<List<GeoSuggestion>> GetAsync(string url, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Mapbox geocoding returned {Status}", res.StatusCode);
            return [];
        }
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return ParseFeatures(doc.RootElement);
    }

    private static List<GeoSuggestion> ParseFeatures(JsonElement collection)
    {
        var list = new List<GeoSuggestion>();
        if (!collection.TryGetProperty("features", out var features)) return list;

        foreach (var f in features.EnumerateArray())
        {
            var props = f.GetProperty("properties");
            var coords = f.GetProperty("geometry").GetProperty("coordinates");
            var address = props.TryGetProperty("full_address", out var full) ? full.GetString()
                        : props.TryGetProperty("name", out var name) ? name.GetString()
                        : null;
            if (address is null) continue;

            string? confidence = props.TryGetProperty("match_code", out var mc) && mc.TryGetProperty("confidence", out var c)
                ? c.GetString() : null;

            list.Add(new GeoSuggestion(address, coords[1].GetDouble(), coords[0].GetDouble(), confidence));
        }
        return list;
    }
}
