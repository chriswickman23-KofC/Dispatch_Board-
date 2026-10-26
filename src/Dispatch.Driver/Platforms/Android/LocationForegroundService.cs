using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using AndroidX.Core.App;
using Dispatch.Driver.Services;
using AndroidLocation = Android.Locations.Location;

namespace Dispatch.Driver.Droid;

/// <summary>
/// A foreground service with a visible "On shift" notification. Android requires this for background
/// location, and it's what keeps tracking alive when the screen is off.
/// </summary>
[Service(ForegroundServiceType = ForegroundService.TypeLocation, Exported = false)]
public sealed class LocationForegroundService : Service, ILocationListener
{
    private const int NotificationId = 1001;
    private const string ChannelId = "on_shift";

    public static bool IsRunning { get; private set; }

    private LocationManager? _manager;
    private LocationQueue? _queue;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (IsRunning) return StartCommandResult.Sticky;
        IsRunning = true;

        CreateChannel();
        var notification = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("On shift")
            .SetContentText("Sharing your location with dispatch")
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetOngoing(true)
            .Build();

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeLocation);
        else
            StartForeground(NotificationId, notification);

        _queue = IPlatformApplication.Current?.Services.GetService<LocationQueue>();
        _manager = (LocationManager?)GetSystemService(LocationService);

        try
        {
            // The fused provider (Android 12+) blends GPS, Wi-Fi, and cell for better battery life
            var provider = OperatingSystem.IsAndroidVersionAtLeast(31) && _manager!.AllProviders.Contains(LocationManager.FusedProvider)
                ? LocationManager.FusedProvider
                : LocationManager.GpsProvider;
            _manager!.RequestLocationUpdates(provider, 30_000, 0f, this, Looper.MainLooper!);
        }
        catch (Java.Lang.SecurityException)
        {
            StopSelf();   // permission was revoked in Settings
        }

        return StartCommandResult.Sticky;
    }

    public void OnLocationChanged(AndroidLocation location) =>
        _ = _queue?.AddAsync(
            location.Latitude,
            location.Longitude,
            location.HasAccuracy ? location.Accuracy : null,
            DateTimeOffset.FromUnixTimeMilliseconds(location.Time));

    public void OnProviderDisabled(string provider) { }
    public void OnProviderEnabled(string provider) { }
    public void OnStatusChanged(string? provider, Availability status, Bundle? extras) { }

    public override void OnDestroy()
    {
        _manager?.RemoveUpdates(this);
        IsRunning = false;
        if (OperatingSystem.IsAndroidVersionAtLeast(24)) StopForeground(StopForegroundFlags.Remove);
        _ = _queue?.FlushAsync();
        base.OnDestroy();
    }

    private void CreateChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var channel = new NotificationChannel(ChannelId, "On shift", NotificationImportance.Low);
        ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(channel);
    }
}

public sealed class AndroidLocationTracker : ILocationTracker
{
    public bool IsRunning => LocationForegroundService.IsRunning;

    public void Start()
    {
        var context = Platform.AppContext;
        var intent = new Intent(context, typeof(LocationForegroundService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
        else context.StartService(intent);
    }

    public void Stop()
    {
        var context = Platform.AppContext;
        context.StopService(new Intent(context, typeof(LocationForegroundService)));
    }
}
