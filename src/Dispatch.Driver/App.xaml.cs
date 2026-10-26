using Dispatch.Driver.Services;

namespace Dispatch.Driver;

public partial class App : Application
{
    private readonly DriverState _state;

    public App(DriverState state)
    {
        InitializeComponent();
        _state = state;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "Courier Driver" };
        // Refresh the route whenever the driver comes back to the app
        window.Resumed += async (_, _) => await _state.OnResumedAsync();
        return window;
    }
}
