namespace AirPlayServer.Core.Notify;

/// <summary>
/// One tracker or a fan-in of every concurrent mirror. Connect is immediate.
/// Disconnect stays debounced per session.
/// </summary>
public interface IMirrorSessionEvents
{
    event EventHandler<MirrorSessionEventArgs>? Changed;

    int DisconnectDebounceMs { get; set; }
}
