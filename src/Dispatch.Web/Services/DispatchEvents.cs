namespace Dispatch.Web.Services;

public enum ChangeKind { Jobs, Drivers, Locations, Etas }

public sealed record DispatchChange(ChangeKind Kind, int? JobId = null, int? DriverId = null);

/// <summary>
/// In-process event bus. Every Blazor Server circuit runs in the same process, so one singleton
/// can notify every open board, map, and tracking page. (Run a single App Service instance.)
/// </summary>
public sealed class DispatchEvents
{
    public event Func<DispatchChange, Task>? Changed;

    public void Publish(DispatchChange change)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Func<DispatchChange, Task> handler in handlers.GetInvocationList())
            _ = InvokeSafely(handler, change);   // one broken circuit can't block the others
    }

    private static async Task InvokeSafely(Func<DispatchChange, Task> handler, DispatchChange change)
    {
        try { await handler(change); }
        catch { /* circuit disconnected; it unsubscribes on dispose */ }
    }
}
