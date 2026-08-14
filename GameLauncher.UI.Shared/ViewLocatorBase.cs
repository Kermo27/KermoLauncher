using Avalonia.Controls;
using Avalonia.Controls.Templates;
using GameLauncher.UI.Shared.ViewModels;

namespace GameLauncher.UI.Shared;

/// <summary>
/// Maps a ViewModel to the View next to it by name and resolves that View from the application
/// container, so views can declare constructor dependencies. Each app supplies its own container.
/// </summary>
public abstract class ViewLocatorBase : IDataTemplate
{
    protected abstract IServiceProvider? Services { get; }

    public Control? Build(object? data)
    {
        if (data is null) return null;

        var viewModelType = data.GetType();
        var viewName = viewModelType.FullName!.Replace("ViewModel", "View");

        // Search the ViewModel's own assembly: Type.GetType only looks in this one.
        var viewType = viewModelType.Assembly.GetType(viewName);
        if (viewType == null)
        {
            return new TextBlock { Text = $"Not found: {viewName}" };
        }

        // Views come from the container only. Falling back to a parameterless constructor used to
        // produce a view with none of its dependencies wired up, which fails much later.
        return Services?.GetService(viewType) as Control
               ?? new TextBlock { Text = $"Not registered: {viewName}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
