namespace AirPlayServer.Core;

public enum AirPlayLogLevel
{
    Information = 0,
    Warning = 1,
    Error = 2,
}

public interface IAirPlayLog
{
    void Log(AirPlayLogLevel level, string message);
}

public sealed class NullAirPlayLog : IAirPlayLog
{
    public static NullAirPlayLog Instance { get; } = new();

    public void Log(AirPlayLogLevel level, string message)
    {
    }
}

/// <summary>
/// Writes receiver logs to stdout. The host uses this so FairPlay, decode, and
/// TapClock connect failures are visible without a separate log sink.
/// </summary>
public sealed class ConsoleAirPlayLog : IAirPlayLog
{
    public static ConsoleAirPlayLog Instance { get; } = new();

    public void Log(AirPlayLogLevel level, string message)
    {
        var prefix = level switch
        {
            AirPlayLogLevel.Error => "error: ",
            AirPlayLogLevel.Warning => "warning: ",
            _ => "",
        };
        Console.WriteLine(prefix + message);
    }
}
