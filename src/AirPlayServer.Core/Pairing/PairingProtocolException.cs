namespace AirPlayServer.Core.Pairing;

public sealed class PairingProtocolException : Exception
{
    public PairingProtocolException(string message, int statusCode = 400, bool closeConnection = false)
        : base(message)
    {
        StatusCode = statusCode;
        CloseConnection = closeConnection;
    }

    public int StatusCode { get; }

    public bool CloseConnection { get; }
}
