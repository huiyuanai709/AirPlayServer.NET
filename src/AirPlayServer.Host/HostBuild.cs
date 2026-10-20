namespace AirPlayServer.Host;

/// <summary>
/// Compile-time package flavor. <c>ui</c> references Avalonia and opens the
/// mirror window. <c>lean</c> does not reference Avalonia at all.
/// </summary>
internal static class HostBuild
{
#if AVALONIA_UI
    public const bool HasAvaloniaWindow = true;

    public const string Flavor = "ui";
#else
    public const bool HasAvaloniaWindow = false;

    public const string Flavor = "lean";
#endif
}
