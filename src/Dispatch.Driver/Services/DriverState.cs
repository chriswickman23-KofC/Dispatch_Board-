using Dispatch.Shared;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Dispatch.Driver.Services;

/// <summary>The driver's profile and route, kept current by SignalR pushes, app resumes, and manual refresh.</summary>
public sealed class DriverState(
    DriverApiClient api,
    AuthService auth,
    ShiftService shift,
    LocationQueue queue,
    ILogger<DriverState> log)
{
    private HubConnection? _hub;
    private bool _loaded;
    private CancellationTokenSource? _bannerCts;

    public DriverProfileDto? Profile { get; private set; }
    public IReadOnlyList<RouteStopDto> Stops { get; private set; } = [];
    public string? Error { get; private set; }
    public string? Banner { get; private set; }

    public event Action? Changed;

    public async Task StartAsync()
    {
        auth.SignedOut -= OnSignedOut;
        auth.SignedOut += OnSignedOut;
        await RefreshAsync();
        await ConnectHubAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            Profile = await api.GetMeAsync();
            var stops = await api.GetRouteAsync();

            if (_loaded && stops.Count > Stops.Count)
            {
                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(500));
                ShowBanner("📳 New stop added by dispatch");
            }
            Stops = stops;
            _loaded = true;
            Error = null;

            await shift.ResumeIfOnShiftAsync(Profile);
            _ = queue.FlushAsync();
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        catch (HttpRequestException)
        {
            Error = "No connection. Showing your last known route.";
        }
        Changed?.Invoke();
    }

    public void SetProfile(DriverProfileDto profile)
    {
        Profile = profile;
        Changed?.Invoke();
    }

    public async Task OnResumedAsync()
    {
        if (Profile is null) return;
        await RefreshAsync();
        if (_hub?.State == HubConnectionState.Disconnected)
        {
            try { await _hub.StartAsync(); }
            catch (Exception ex) { log.LogInformation("Hub reconnect failed: {Message}", ex.Message); }
        }
    }

    private async Task ConnectHubAsync()
    {
        if (_hub is not null) return;

        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(AppConfig.ServerUrl), "hubs/driver"),
                o => o.AccessTokenProvider = () => auth.GetValidAccessTokenAsync())
            .WithAutomaticReconnect()
            .Build();

        _hub.On(DriverHubEvents.RouteChanged, () => MainThread.InvokeOnMainThreadAsync(RefreshAsync));
        _hub.Reconnected += _ => MainThread.InvokeOnMainThreadAsync(RefreshAsync);

        try { await _hub.StartAsync(); }
        catch (Exception ex) { log.LogInformation("Hub connect failed, will retry on resume: {Message}", ex.Message); }
    }

    private void OnSignedOut()
    {
        shift.StopTracking();
        _ = _hub?.DisposeAsync();
        _hub = null;
        Profile = null;
        Stops = [];
        _loaded = false;
        Changed?.Invoke();
    }

    private void ShowBanner(string text)
    {
        _bannerCts?.Cancel();
        _bannerCts = new CancellationTokenSource();
        var token = _bannerCts.Token;
        Banner = text;
        _ = Task.Delay(3000, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Banner = null;
            MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
        }, TaskScheduler.Default);
    }
}
