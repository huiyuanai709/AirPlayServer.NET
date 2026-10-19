namespace AirPlayServer.Core.Video;

/// <summary>
/// When a synchronous hardware Media Foundation transform should be released.
/// Draining that transform to force the first picture flushes its reference
/// frames, so later slices produce nothing while the driver threads keep
/// running. A startup delay of one access unit is normal. Two misses with no
/// picture, or any miss after a picture, means the transform is stuck.
/// </summary>
internal static class HardwareDecodeStall
{
    internal const int MissesWithNoPicture = 2;

    internal const int MissesAfterPicture = 1;

    public static bool ShouldAbandon(int picturesProduced, int consecutiveMisses)
    {
        if (picturesProduced < 0 || consecutiveMisses <= 0)
        {
            return false;
        }

        if (picturesProduced == 0)
        {
            return consecutiveMisses >= MissesWithNoPicture;
        }

        return consecutiveMisses >= MissesAfterPicture;
    }
}
