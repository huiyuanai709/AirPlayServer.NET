using AirPlayServer.Core.Pairing;

namespace AirPlayServer.Core.Tests;

public sealed class PairingTests
{
    [Fact]
    public void Transient_pair_verify_round_trip()
    {
        using var receiverKey = PairingIdentity.Generate();
        using var senderKey = PairingIdentity.Generate();
        using var session = new PairingSession(receiverKey);
        using var sender = new PairingSender(senderKey);

        var setup = session.PairSetup(sender.CreateSetupRequest());
        Assert.Equal(PairingPhase.Setup, session.Phase);
        Assert.Equal(receiverKey.PublicKey.ToArray(), setup);
        sender.AcceptSetup(setup);

        var finish = sender.AcceptVerifyStart(session.PairVerifyStart(sender.CreateVerifyStart()));
        session.PairVerifyFinish(finish);

        Assert.Equal(PairingPhase.Finished, session.Phase);
        Assert.Equal(senderKey.PublicKey.ToArray(), session.PeerPublicKey.ToArray());
    }

    [Fact]
    public void Pair_verify_can_start_without_pair_setup()
    {
        using var receiverKey = PairingIdentity.Generate();
        using var senderKey = PairingIdentity.Generate();
        using var session = new PairingSession(receiverKey);
        using var sender = new PairingSender(senderKey);

        sender.AcceptSetup(receiverKey.PublicKey.Span);
        session.PairVerifyFinish(sender.AcceptVerifyStart(session.PairVerifyStart(sender.CreateVerifyStart())));
        Assert.Equal(PairingPhase.Finished, session.Phase);
    }

    [Fact]
    public void Bad_signature_is_rejected()
    {
        using var receiverKey = PairingIdentity.Generate();
        using var senderKey = PairingIdentity.Generate();
        using var session = new PairingSession(receiverKey);
        using var sender = new PairingSender(senderKey);
        sender.AcceptSetup(session.PairSetup(sender.CreateSetupRequest()));
        var finish = sender.AcceptVerifyStart(session.PairVerifyStart(sender.CreateVerifyStart()));
        finish[10] ^= 0x01;

        var error = Assert.Throws<PairingProtocolException>(() => session.PairVerifyFinish(finish));
        Assert.Equal(403, error.StatusCode);
        Assert.Equal(PairingPhase.Handshake, session.Phase);
    }

    [Fact]
    public void Pair_setup_rejects_the_wrong_length()
    {
        using var receiverKey = PairingIdentity.Generate();
        using var session = new PairingSession(receiverKey);
        var error = Assert.Throws<PairingProtocolException>(() => session.PairSetup(new byte[31]));
        Assert.Equal(400, error.StatusCode);
    }
}
