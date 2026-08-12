using Avalonia.Controls;
using Avalonia.Controls.Templates;
using GameLauncher.UI.ViewModels;

namespace GameLauncher.UI;

public class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null) return null;

        var name = data.GetType().FullName!.Replace("ViewModel", "View");
        var type = Type.GetType(name);
        if (type == null)
        {
            return new TextBlock { Text = $"Not Found: {name}" };
        }

        // Widoki mogą mieć zależności, więc najpierw pytamy kontener,
        // a bezparametrowy konstruktor jest tylko awaryjnym wyjściem.
        var fromContainer = App.Services?.GetService(type);
        if (fromContainer is Control resolved) return resolved;

        return Activator.CreateInstance(type) as Control
               ?? new TextBlock { Text = $"Cannot create: {name}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
