using GameLauncher.UI.Shared;

namespace GameLauncher.UI;

public sealed class ViewLocator : ViewLocatorBase
{
    protected override IServiceProvider? Services => App.Services;
}
