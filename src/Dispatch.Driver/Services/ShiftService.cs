using Dispatch.Shared;

namespace Dispatch.Driver.Services;

public sealed class ShiftService(DriverApiClient api, ILocationTracker tracker, LocationQueue queue)
{
    public async Task<DriverProfileDto> ClockInAsync()
    {
        if (await Permissions.RequestAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted)
            throw new ApiException("Location permission is required to clock in.");

        if (await Permissions.RequestAsync<Permissions.LocationAlways>() != PermissionStatus.Granted)
            throw new ApiException("Set location to \"Always\" (iPhone) or \"Allow all the time\" (Android) so dispatch can see you while the phone is locked.");

        if (DeviceInfo.Platform == DevicePlatform.Android && OperatingSystem.IsAndroidVersionAtLeast(33))
            await Permissions.RequestAsync<Permissions.PostNotifications>();   // for the "On shift" notification

        tracker.Start();
        try
        {
            return await api.ClockInAsync();
        }
        catch
        {
            tracker.Stop();
            throw;
        }
    }

    public async Task<DriverProfileDto> ClockOutAsync()
    {
        tracker.Stop();
        await queue.FlushAsync();
        return await api.ClockOutAsync();
    }

    /// <summary>Restarts tracking after a phone restart or app update if the driver is still on shift.</summary>
    public async Task ResumeIfOnShiftAsync(DriverProfileDto profile)
    {
        if (profile.Status == DriverStatus.Offline || tracker.IsRunning) return;
        if (await Permissions.CheckStatusAsync<Permissions.LocationAlways>() == PermissionStatus.Granted)
            tracker.Start();
    }

    public void StopTracking() => tracker.Stop();
}
