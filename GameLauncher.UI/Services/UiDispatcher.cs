using Avalonia.Threading;

namespace GameLauncher.UI.Services;

/// <summary>
/// A thin layer over the UI thread. ViewModels call this instead of the static
/// Dispatcher.UIThread, which makes them testable without a running Avalonia application.
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
    Task InvokeAsync(Action action);
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
}
