using System.Security.Cryptography;
using System.Text;
using AirPlayServer.Core.Discovery;

namespace AirPlayServer.Core.Protocol;

internal static class ServerInfo
{
    public static string Xml(ReceiverAdvertisement advertisement)
    {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n<dict>\n" +
            "<key>deviceid</key>\n<string>" + advertisement.DeviceId + "</string>\n" +
            "<key>features</key>\n<integer>" + AirPlayCompatibility.FeaturesLow + "</integer>\n" +
            "<key>model</key>\n<string>" + AirPlayCompatibility.Model + "</string>\n" +
            "<key>protovers</key>\n<string>1.0</string>\n" +
            "<key>srcvers</key>\n<string>" + AirPlayCompatibility.SourceVersion + "</string>\n" +
            "</dict>\n</plist>\n";
    }

    public static string NotReadyXml()
        => """
           <?xml version="1.0" encoding="UTF-8"?>
           <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
           <plist version="1.0">
           <dict>
           <key>readyToPlay</key>
           <false/>
           </dict>
           </plist>
           """;

    public static byte[] Binary(ReceiverAdvertisement advertisement, ReadOnlySpan<byte> publicKey)
    {
        var display = new PlistDictionary();
        display.Add("width", new PlistInteger(advertisement.DisplayWidth));
        display.Add("height", new PlistInteger(advertisement.DisplayHeight));
        display.Add("widthPixels", new PlistInteger(advertisement.DisplayWidth));
        display.Add("heightPixels", new PlistInteger(advertisement.DisplayHeight));
        // Upstream stores the frame duration here, not the refresh rate in hertz.
        display.Add("refreshRate", new PlistReal(1d / Math.Max(1, advertisement.MaxFramesPerSecond)));
        display.Add("maxFPS", new PlistInteger(advertisement.MaxFramesPerSecond));
        display.Add("uuid", new PlistString(DisplayUuid(advertisement.DeviceId)));
        var displays = new PlistArray();
        displays.Add(display);

        var root = new PlistDictionary();
        root.Add("deviceID", new PlistString(advertisement.DeviceId));
        root.Add("features", new PlistInteger(AirPlayCompatibility.FeaturesLow));
        root.Add("model", new PlistString(AirPlayCompatibility.Model));
        root.Add("name", new PlistString(advertisement.InstanceName));
        root.Add("sourceVersion", new PlistString(AirPlayCompatibility.SourceVersion));
        root.Add("statusFlags", new PlistInteger(4));
        root.Add("macAddress", new PlistString(advertisement.DeviceId));
        root.Add("pk", new PlistData(publicKey.ToArray()));
        root.Add("displays", displays);
        return BinaryPropertyList.Write(root);
    }

    public static string PlainStatus(ReceiverAdvertisement advertisement, bool discovery)
    {
        var builder = new StringBuilder();
        builder.AppendLine("AirPlayServer.NET");
        builder.Append("deviceid: ").AppendLine(advertisement.DeviceId);
        builder.Append("airplay: ").Append(advertisement.AirPlayPort).AppendLine();
        builder.Append("raop: ").Append(advertisement.RaopPort).AppendLine();
        builder.Append("discovery: ").AppendLine(discovery ? "enabled" : "disabled");
        builder.AppendLine("media: not implemented");
        return builder.ToString();
    }

    private static string DisplayUuid(string deviceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
}
