using Dispatch.Driver.Services;
using Microsoft.AspNetCore.Components;

namespace Dispatch.Driver.Components.Shared;

/// <summary>Re-renders whenever the route or profile changes.</summary>
public abstract class StateAwareComponent : ComponentBase, IDisposable
{
    [Inject] protected DriverState State { get; set; } = default!;

    protected override void OnInitialized() => State.Changed += OnStateChanged;

    private void OnStateChanged() => InvokeAsync(StateHasChanged);

    public virtual void Dispose() => State.Changed -= OnStateChanged;
}
