using CoreLocation;
using Dispatch.Driver.Services;

namespace Dispatch.Driver.iOSPlatform;

public sealed class IosLocationTracker(LocationQueue queue) : ILocationTracker
{
    private CLLocationManager? _manager;

    public bool IsRunning { get; private set; }

    public void Start()
    {
        // Created on first use, which is always from the UI thread as iOS requires
        if (_manager is null)
        {
            _manager = new CLLocationManager
            {
                DesiredAccuracy = CLLocation.AccuracyNearestTenMeters,
                // Report even small movements so drivers waiting at a stop don't look "stale";
                // the queue thins this to one point every ~25 seconds
                DistanceFilter = CLLocationDistance.FilterNone,
                PausesLocationUpdatesAutomatically = false,
                AllowsBackgroundLocationUpdates = true,
                ShowsBackgroundLocationIndicator = true,
                ActivityType = CLActivityType.AutomotiveNavigation,
            };
            _manager.LocationsUpdated += (_, e) =>
            {
                foreach (var l in e.Locations)
                {
                    _ = queue.AddAsync(l.Coordinate.Latitude, l.Coordinate.Longitude,
                        l.HorizontalAccuracy, new DateTimeOffset((DateTime)l.Timestamp));
                }
            };
        }
        _manager.StartUpdatingLocation();
        IsRunning = true;
    }

    public void Stop()
    {
        _manager?.StopUpdatingLocation();
        IsRunning = false;
    }
}
