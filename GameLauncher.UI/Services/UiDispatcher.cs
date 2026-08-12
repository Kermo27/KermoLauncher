using Avalonia.Threading;

namespace GameLauncher.UI.Services;

/// <summary>
/// Cienka warstwa nad wątkiem UI. ViewModele wołają ją zamiast statycznego Dispatcher.UIThread,
/// więc dają się testować bez uruchomionej aplikacji Avalonia.
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
