using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AirPlayServer.Core.Media;

/// <summary>
/// Loads an optional native media-key plugin. Native AOT cannot load a managed assembly
/// at runtime, so this override is a C ABI in a DLL or shared library:
/// <code>
/// int AirPlay_TryUnwrapContentKey(const uint8_t* wrapped, int wrappedLength, uint8_t* contentKey16);
/// int AirPlay_FairPlaySetup(const uint8_t* request, int requestLength, uint8_t* response, int responseCapacity, int* written);
/// </code>
/// Either export may be absent. A missing export, a missing file, or a 0 return stays
/// fail-closed for that call. The host uses in-process <see cref="FairPlayKeys"/> unless
/// this path is set.
/// </summary>
internal sealed class NativeMediaKeyModule : IMediaKeyUnwrapper, IFairPlaySetup, IDisposable
{
    private readonly nint _library;
    private readonly UnwrapExport? _unwrap;
    private readonly FairPlayExport? _fairPlay;
    private int _disposed;

    private NativeMediaKeyModule(nint library, UnwrapExport? unwrap, FairPlayExport? fairPlay)
    {
        _library = library;
        _unwrap = unwrap;
        _fairPlay = fairPlay;
    }

    public bool HasUnwrap => _unwrap is not null;

    public bool HasFairPlay => _fairPlay is not null;

    public static bool TryLoad(string path, out NativeMediaKeyModule? module, out string error)
    {
        module = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Media-key plugin path is empty.";
            return false;
        }

        if (!File.Exists(path))
        {
            error = "Media-key plugin was not found: " + path;
            return false;
        }

        if (!NativeLibrary.TryLoad(path, out var library))
        {
            error = "Media-key plugin could not be loaded: " + path;
            return false;
        }

        UnwrapExport? unwrap = null;
        FairPlayExport? fairPlay = null;
        if (NativeLibrary.TryGetExport(library, "AirPlay_TryUnwrapContentKey", out var unwrapExport))
        {
            unwrap = Marshal.GetDelegateForFunctionPointer<UnwrapExport>(unwrapExport);
        }

        if (NativeLibrary.TryGetExport(library, "AirPlay_FairPlaySetup", out var fairPlayExport))
        {
            fairPlay = Marshal.GetDelegateForFunctionPointer<FairPlayExport>(fairPlayExport);
        }

        if (unwrap is null && fairPlay is null)
        {
            NativeLibrary.Free(library);
            error = "Media-key plugin does not export AirPlay_TryUnwrapContentKey or AirPlay_FairPlaySetup.";
            return false;
        }

        module = new NativeMediaKeyModule(library, unwrap, fairPlay);
        error = "";
        return true;
    }

    public bool TryUnwrap(ReadOnlySpan<byte> wrappedKey, Span<byte> contentKey)
    {
        if (_unwrap is null || contentKey.Length < 16 || wrappedKey.IsEmpty)
        {
            return false;
        }

        var wrapped = wrappedKey.ToArray();
        var key = new byte[16];
        var wrappedHandle = GCHandle.Alloc(wrapped, GCHandleType.Pinned);
        var keyHandle = GCHandle.Alloc(key, GCHandleType.Pinned);
        try
        {
            if (_unwrap(wrappedHandle.AddrOfPinnedObject(), wrapped.Length, keyHandle.AddrOfPinnedObject()) == 0)
            {
                return false;
            }

            key.CopyTo(contentKey);
            return true;
        }
        finally
        {
            keyHandle.Free();
            wrappedHandle.Free();
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public bool TryRespond(ReadOnlySpan<byte> request, Span<byte> response, out int written)
    {
        written = 0;
        if (_fairPlay is null || response.IsEmpty)
        {
            return false;
        }

        var requestBytes = request.IsEmpty ? new byte[1] : request.ToArray();
        var responseBytes = new byte[response.Length];
        var writtenBytes = new int[1];
        var requestHandle = GCHandle.Alloc(requestBytes, GCHandleType.Pinned);
        var responseHandle = GCHandle.Alloc(responseBytes, GCHandleType.Pinned);
        var writtenHandle = GCHandle.Alloc(writtenBytes, GCHandleType.Pinned);
        try
        {
            var result = _fairPlay(
                requestHandle.AddrOfPinnedObject(),
                request.Length,
                responseHandle.AddrOfPinnedObject(),
                response.Length,
                writtenHandle.AddrOfPinnedObject());
            if (result == 0 || writtenBytes[0] <= 0 || writtenBytes[0] > response.Length)
            {
                return false;
            }

            responseBytes.AsSpan(0, writtenBytes[0]).CopyTo(response);
            written = writtenBytes[0];
            return true;
        }
        finally
        {
            writtenHandle.Free();
            responseHandle.Free();
            requestHandle.Free();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        NativeLibrary.Free(_library);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UnwrapExport(nint wrapped, int wrappedLength, nint contentKey);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FairPlayExport(nint request, int requestLength, nint response, int responseCapacity, nint written);
}
