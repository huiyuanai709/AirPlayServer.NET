using Avalonia;

namespace AirPlayServer.Host.Display;

/// <summary>
/// Code-only Avalonia application. There is no XAML, no style lookup, and no
/// reflection-based control catalog. The mirror window sets its own brushes.
/// </summary>
internal sealed class MirrorApplication : Application
{
    public override void Initialize()
    {
    }
}
