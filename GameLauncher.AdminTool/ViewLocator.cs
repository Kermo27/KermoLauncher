using GameLauncher.UI.Shared;

namespace GameLauncher.AdminTool;

public sealed class ViewLocator : ViewLocatorBase
{
    protected override IServiceProvider? Services => App.Services;
}
