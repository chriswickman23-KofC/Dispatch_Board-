using System.Collections.Concurrent;

namespace Dispatch.Web.Services;

public sealed record PresenceEntry(string UserId, string Name, int? ViewingJobId);

/// <summary>Who's online on the board and which job each dispatcher is looking at.</summary>
public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<Guid, PresenceEntry> _entries = new();

    public event Action? Changed;

    public IEnumerable<PresenceEntry> All => _entries.Values;

    /// <summary>One avatar per person, even with the board open in two tabs.</summary>
    public IReadOnlyList<PresenceEntry> Online =>
        _entries.Values.GroupBy(e => e.UserId).Select(g => g.First()).OrderBy(e => e.Name).ToList();

    public void Set(Guid key, PresenceEntry entry)
    {
        _entries[key] = entry;
        Notify();
    }

    public void Remove(Guid key)
    {
        if (_entries.TryRemove(key, out _)) Notify();
    }

    private void Notify()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); } catch { /* disconnected circuit */ }
        }
    }
}
