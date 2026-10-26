using System.Collections.Concurrent;

namespace Dispatch.Web.Services;

public sealed record SelectionState(int? DriverId, int? JobId);

/// <summary>Keeps each dispatcher's board and map windows pointed at the same driver and job.</summary>
public sealed class SelectionSync
{
    private readonly ConcurrentDictionary<string, SelectionState> _byUser = new();

    public event Action<string, SelectionState>? Changed;

    public SelectionState Get(string userId) =>
        _byUser.TryGetValue(userId, out var s) ? s : new SelectionState(null, null);

    public void SetDriver(string userId, int? driverId) => Update(userId, s => s with { DriverId = driverId });
    public void SetJob(string userId, int? jobId) => Update(userId, s => s with { JobId = jobId });

    private void Update(string userId, Func<SelectionState, SelectionState> change)
    {
        var next = _byUser.AddOrUpdate(userId, _ => change(new SelectionState(null, null)), (_, current) => change(current));
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Action<string, SelectionState> handler in handlers.GetInvocationList())
        {
            try { handler(userId, next); } catch { /* disconnected circuit */ }
        }
    }
}
