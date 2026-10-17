namespace AirPlayServer.Core.Tests;

internal static class Hex
{
    public static byte[] Parse(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal));
}
