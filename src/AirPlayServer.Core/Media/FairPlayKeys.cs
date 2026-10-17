using AirPlayFairPlay;

namespace AirPlayServer.Core.Media;

/// <summary>
/// One in-process FairPlay session. <see cref="TryRespond"/> and <see cref="TryUnwrap"/>
/// share that session, so a <c>POST /fp-setup</c> handshake is still present when SETUP
/// unwraps a 72-byte ekey.
/// </summary>
public sealed class FairPlayKeys : IMediaKeyUnwrapper, IFairPlaySetup
{
    private readonly FairPlaySession _session;

    public FairPlayKeys()
        : this(new FairPlaySession())
    {
    }

    public FairPlayKeys(FairPlaySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public bool TryUnwrap(ReadOnlySpan<byte> wrappedKey, Span<byte> contentKey)
        => _session.TryUnwrap(wrappedKey, contentKey);

    public bool TryRespond(ReadOnlySpan<byte> request, Span<byte> response, out int written)
        => _session.TryRespond(request, response, out written);
}
