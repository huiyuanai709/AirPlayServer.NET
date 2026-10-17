namespace AirPlayServer.Core.Discovery;

internal static class ServiceText
{
    public static IReadOnlyList<string> AirPlay(ReceiverAdvertisement advertisement) =>
    [
        "srcvers=" + AirPlayCompatibility.SourceVersion,
        "deviceid=" + advertisement.DeviceId,
        "features=" + AirPlayCompatibility.FeaturesText,
        "model=" + AirPlayCompatibility.Model,
        "flags=0x4",
        "pw=false",
        "vv=2",
        "pk=" + advertisement.PublicKeyBase64,
    ];

    public static IReadOnlyList<string> Raop(ReceiverAdvertisement advertisement) =>
    [
        "txtvers=1",
        "ch=2",
        "cn=0,1,3",
        "et=0,3,5",
        "sv=false",
        "da=true",
        "sr=44100",
        "ss=16",
        "pw=false",
        "vn=3",
        "tp=TCP,UDP",
        "md=0,1,2",
        "vs=" + AirPlayCompatibility.SourceVersion,
        "sm=false",
        "ek=1",
        "sf=0x4",
        "ft=" + AirPlayCompatibility.FeaturesText,
        "am=" + AirPlayCompatibility.Model,
    ];
}
