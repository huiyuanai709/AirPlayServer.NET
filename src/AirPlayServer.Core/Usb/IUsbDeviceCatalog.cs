namespace AirPlayServer.Core.Usb;

internal sealed class UsbPhone
{
    public required string Id { get; init; }

    public string? Product { get; init; }

    public bool QuickTimeActive { get; init; }
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
