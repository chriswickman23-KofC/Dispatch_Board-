using Dispatch.Shared;

namespace Dispatch.Driver.Services;

public static class Ui
{
    public static string WindowText(RouteStopDto s) =>
        s.JobType == JobType.OnDemand ? "ASAP"
        : s.WindowStart is { } ws ? $"{ws.ToLocalTime():h:mm tt}–{s.WindowEnd?.ToLocalTime():h:mm tt}"
        : "Today";

    public static string StatusClass(StopStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>Native yes/no dialog. (Named DisplayAlertAsync in newer .NET MAUI versions.)</summary>
    public static Task<bool> ConfirmAsync(string title, string message, string accept) =>
        Application.Current!.Windows[0].Page!.DisplayAlert(title, message, accept, "Cancel");
}
