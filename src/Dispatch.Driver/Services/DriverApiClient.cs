using System.Globalization;
using System.Net.Http.Json;
using Dispatch.Shared;

namespace Dispatch.Driver.Services;

public sealed class DriverApiClient(IHttpClientFactory httpFactory)
{
    private HttpClient Http => httpFactory.CreateClient("api");

    public Task<DriverProfileDto> GetMeAsync() => GetAsync<DriverProfileDto>("api/driver/me");
    public Task<List<RouteStopDto>> GetRouteAsync() => GetAsync<List<RouteStopDto>>("api/driver/route");
    public Task<DriverProfileDto> ClockInAsync() => PostAsync<DriverProfileDto>("api/driver/clock-in", null);
    public Task<DriverProfileDto> ClockOutAsync() => PostAsync<DriverProfileDto>("api/driver/clock-out", null);

    public async Task<int> SendLocationsAsync(IReadOnlyList<LocationDto> points) =>
        (await PostAsync<LocationsAccepted>("api/driver/locations", points)).Accepted;

    public async Task SetStopStatusAsync(int stopId, StopStatus status, string? reason = null)
    {
        using var res = await Http.PostAsJsonAsync($"api/driver/stops/{stopId}/status", new StopStatusRequest(status, reason), Json.Options);
        await EnsureOkAsync(res);
    }

    public async Task CompleteDeliveryAsync(int stopId, byte[] photoJpeg, byte[]? signaturePng, string? recipientName, Location? location)
    {
        using var form = new MultipartFormDataContent();

        var photo = new ByteArrayContent(photoJpeg);
        photo.Headers.ContentType = new("image/jpeg");
        form.Add(photo, "photo", "photo.jpg");

        if (signaturePng is not null)
        {
            var signature = new ByteArrayContent(signaturePng);
            signature.Headers.ContentType = new("image/png");
            form.Add(signature, "signature", "signature.png");
        }
        if (!string.IsNullOrWhiteSpace(recipientName)) form.Add(new StringContent(recipientName.Trim()), "recipientName");
        if (location is not null)
        {
            form.Add(new StringContent(location.Latitude.ToString(CultureInfo.InvariantCulture)), "lat");
            form.Add(new StringContent(location.Longitude.ToString(CultureInfo.InvariantCulture)), "lng");
        }

        using var res = await Http.PostAsync($"api/driver/stops/{stopId}/complete", form);
        await EnsureOkAsync(res);
    }

    private async Task<T> GetAsync<T>(string url)
    {
        using var res = await Http.GetAsync(url);
        await EnsureOkAsync(res);
        return (await res.Content.ReadFromJsonAsync<T>(Json.Options))!;
    }

    private async Task<T> PostAsync<T>(string url, object? body)
    {
        using var res = body is null ? await Http.PostAsync(url, null) : await Http.PostAsJsonAsync(url, body, Json.Options);
        await EnsureOkAsync(res);
        return (await res.Content.ReadFromJsonAsync<T>(Json.Options))!;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        var message = await AuthService.TryReadProblemAsync(res) ?? "Something went wrong. Please try again.";
        throw new ApiException(message, res.StatusCode);
    }
}
