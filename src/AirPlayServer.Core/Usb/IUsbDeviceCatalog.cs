namespace AirPlayServer.Core.Usb;

internal sealed class UsbPhone
{
    public required string Id { get; init; }

    public string? Product { get; init; }

    public bool QuickTimeActive { get; init; }

    /// <summary>
    /// Stable libusb0 filter path (<c>\\.\libusb0-…</c>). Empty on libusb-1.0.
    /// Two serial strings for the same node are still one phone.
    /// </summary>
    public string? Node { get; init; }
}

/// <summary>
/// One claimed QuickTime bulk pipe. <see cref="Read"/> returns a positive
/// count, zero on timeout, or a negative value when the phone is gone.
/// </summary>
internal interface IUsbBulkChannel : IDisposable
{
    string Id { get; }

    string? Product { get; }

    int Read(Span<byte> buffer, int timeoutMs);

    void Write(ReadOnlySpan<byte> data);
}

internal interface IUsbDeviceCatalog
{
    IReadOnlyList<UsbPhone> List();

    IUsbBulkChannel? TryOpen(UsbPhone phone, CancellationToken cancellationToken);
}
