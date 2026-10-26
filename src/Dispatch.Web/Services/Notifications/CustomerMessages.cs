namespace Dispatch.Web.Services.Notifications;

public static class CustomerMessages
{
    /// <summary>Driver failure reasons are written for dispatch; this turns them into customer-friendly wording.</summary>
    public static string? FriendlyFailure(string? reason) => reason switch
    {
        "Nobody available" => "no one was available to receive it",
        "Couldn't access building" => "we couldn't access the building",
        "Wrong or missing address" => "we couldn't find the address",
        "Recipient refused" => "the delivery was declined",
        _ => null,
    };

    /// <summary>"around 2:45 PM": rounded up to 5 minutes so it doesn't look falsely precise.</summary>
    public static string Around(DateTimeOffset localTime)
    {
        var extra = (5 - localTime.Minute % 5) % 5;
        var rounded = localTime.AddMinutes(extra).AddSeconds(-localTime.Second);
        return $"around {rounded:h:mm tt}";
    }
}
