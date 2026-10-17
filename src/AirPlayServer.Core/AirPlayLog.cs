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
