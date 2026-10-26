namespace Dispatch.Driver.Services;

public interface ILocationTracker
{
    bool IsRunning { get; }
    void Start();
    void Stop();
}
