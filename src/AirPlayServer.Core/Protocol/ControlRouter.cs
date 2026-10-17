using System.Net;
using System.Text;
using AirPlayServer.Core.Discovery;
using AirPlayServer.Core.Media;
using AirPlayServer.Core.Pairing;

namespace AirPlayServer.Core.Protocol;

internal sealed class ControlRouter
{
    private readonly ReceiverAdvertisement _advertisement;
    private readonly byte[] _publicKey;
    private readonly bool _discoveryEnabled;
    private readonly IAirPlayLog _log;
    private readonly MirrorRuntime _mirror;
    private readonly IFairPlaySetup? _fairPlay;

    public ControlRouter(ReceiverAdvertisement advertisement, ReadOnlySpan<byte> publicKey, bool discoveryEnabled, IAirPlayLog log, MirrorRuntime mirror, IFairPlaySetup? fairPlay = null)
    {
        _advertisement = advertisement;
        _publicKey = publicKey.ToArray();
        _discoveryEnabled = discoveryEnabled;
        _log = log;
        _mirror = mirror;
        _fairPlay = fairPlay;
    }

    public Http1Response Dispatch(Http1Request request, PairingSession session, EndPoint? remote = null)
    {
        try
        {
            var response = DispatchCore(request, session, remote);
            _log.Log(AirPlayLogLevel.Information, $"{request.Method} {request.Target} -> {response.Status}");
            return response;
        }
        catch (PairingProtocolException ex)
        {
            _log.Log(AirPlayLogLevel.Warning, $"{request.Method} {request.Path} rejected: {ex.Message}");
            return Http1Response.Text(ex.StatusCode, ex.Message, ex.CloseConnection);
        }
    }

    private Http1Response DispatchCore(Http1Request request, PairingSession session, EndPoint? remote)
    {
        if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            return Http1Response.Empty(200, extra:
            [
                ("Public", "ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER, POST"),
            ]);
        }

        if (request.Method.Equals("ANNOUNCE", StringComparison.OrdinalIgnoreCase)
            || request.Method.Equals("SETUP", StringComparison.OrdinalIgnoreCase)
            || request.Method.Equals("RECORD", StringComparison.OrdinalIgnoreCase)
            || request.Method.Equals("PAUSE", StringComparison.OrdinalIgnoreCase)
            || request.Method.Equals("FLUSH", StringComparison.OrdinalIgnoreCase)
            || request.Method.Equals("TEARDOWN", StringComparison.OrdinalIgnoreCase)
            || request.Method.Equals("SET_PARAMETER", StringComparison.OrdinalIgnoreCase)
            || request.Method.Equals("GET_PARAMETER", StringComparison.OrdinalIgnoreCase))
        {
            return _mirror.HandleMedia(request, session, remote);
        }

        if (Is(request, "POST", "/pair-setup"))
        {
            var body = session.PairSetup(request.Body);
            return Http1Response.Bytes(200, "application/octet-stream", body);
        }

        if (Is(request, "POST", "/pair-verify"))
        {
            if (request.Body.Length == 0)
            {
                throw new PairingProtocolException("pair-verify body is empty.");
            }

            if (request.Body[0] == 1)
            {
                return Http1Response.Bytes(200, "application/octet-stream", session.PairVerifyStart(request.Body));
            }

            if (request.Body[0] == 0)
            {
                session.PairVerifyFinish(request.Body);
                _mirror.NotePairingSecret(session.SharedSecret.Span);
                return Http1Response.Bytes(200, "application/octet-stream", []);
            }

            throw new PairingProtocolException("pair-verify type is not 0 or 1.");
        }

        if (Is(request, "GET", "/server-info"))
        {
            return Http1Response.Bytes(200, "text/x-apple-plist+xml", Encoding.UTF8.GetBytes(ServerInfo.Xml(_advertisement)));
        }

        if ((Is(request, "GET", "/info") || Is(request, "POST", "/info")))
        {
            return Http1Response.Bytes(200, "application/x-apple-binary-plist", ServerInfo.Binary(_advertisement, _publicKey));
        }

        if (Is(request, "GET", "/playback-info"))
        {
            return Http1Response.Bytes(200, "text/x-apple-plist+xml", Encoding.UTF8.GetBytes(ServerInfo.NotReadyXml()));
        }

        if (Is(request, "POST", "/reverse"))
        {
            _log.Log(AirPlayLogLevel.Information, "Reverse event channel opened. Events are not interpreted yet.");
            return Http1Response.Empty(101, reverse: true);
        }

        if (Is(request, "POST", "/fp-setup"))
        {
            if (_fairPlay is not null)
            {
                var buffer = new byte[4096];
                if (_fairPlay.TryRespond(request.Body, buffer, out var written) && written is > 0 and <= 4096)
                {
                    _mirror.RetryPendingFairPlay();
                    return Http1Response.Bytes(200, "application/octet-stream", buffer[..written]);
                }
            }

            return NotImplemented("FairPlay setup is not implemented.");
        }

        if (Is(request, "POST", "/pair-setup-pin"))
        {
            return NotImplemented("PIN/SRP pairing is not implemented in this slice.");
        }

        if (Is(request, "POST", "/play"))
        {
            return NotImplemented("URL and HLS playback are not implemented.");
        }

        if (Is(request, "POST", "/feedback"))
        {
            return Http1Response.Empty(200);
        }

        if (Is(request, "GET", "/"))
        {
            return Http1Response.Text(200, ServerInfo.PlainStatus(_advertisement, _discoveryEnabled, _fairPlay is not null));
        }

        return Http1Response.Text(404, "No route for " + request.Method + " " + request.Path);
    }

    private static Http1Response NotImplemented(string message) => Http1Response.Text(501, message);

    private static bool Is(Http1Request request, string method, string path)
        => request.Method.Equals(method, StringComparison.OrdinalIgnoreCase)
           && request.Path.Equals(path, StringComparison.OrdinalIgnoreCase);
}
