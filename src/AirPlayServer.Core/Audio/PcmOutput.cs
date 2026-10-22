using System.Runtime.InteropServices;

namespace AirPlayServer.Core.Audio;

internal interface IPcmOutput : IDisposable
{
    bool IsOpen { get; }

    string Name { get; }

    void Write(ReadOnlySpan<short> interleaved, int channels, int sampleRate);

    void Flush();
}

internal sealed class DiscardPcmOutput : IPcmOutput
{
    public bool IsOpen => false;

    public string Name => "discard";

    public void Write(ReadOnlySpan<short> interleaved, int channels, int sampleRate)
    {
    }

    public void Flush()
    {
    }

    public void Dispose()
    {
    }
}

internal static class SystemPcmOutput
{
    public static IPcmOutput Create(IAirPlayLog log)
        => Create(log, 44100, 2, usb: false);

    /// <summary>
    /// Opens the default playback device at <paramref name="sampleRate"/> and
    /// <paramref name="channels"/>. Wireless AirPlay calls the parameterless
    /// overload and stays at 44.1 kHz stereo. USB LPCM passes the rate from
    /// the QuickTime audio format, usually 48 kHz.
    /// </summary>
    public static IPcmOutput Create(IAirPlayLog log, int sampleRate, int channels, bool usb)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (sampleRate is < 8000 or > 192000)
        {
            sampleRate = usb ? 48000 : 44100;
        }

        if (channels is < 1 or > 2)
        {
            channels = 2;
        }

        if (OperatingSystem.IsWindows())
        {
            var wasapi = WasapiPcmOutput.TryCreate(log, sampleRate, channels, usb);
            if (wasapi is not null)
            {
                return wasapi;
            }
        }
        else
        {
            var pulse = PulsePcmOutput.TryCreate(log, sampleRate, channels, usb);
            if (pulse is not null)
            {
                return pulse;
            }

            var alsa = AlsaPcmOutput.TryCreate(log, sampleRate, channels, usb);
            if (alsa is not null)
            {
                return alsa;
            }
        }

        log.Log(AirPlayLogLevel.Warning, FailureText(usb));
        return new DiscardPcmOutput();
    }

    private static string FailureText(bool usb)
    {
        if (usb && OperatingSystem.IsWindows())
        {
            return "USB 伴音（LPCM）已解开，但打不开系统默认播放设备（WASAPI）。PCM 被丢掉，画面不受影响。USB LPCM was decoded, but the default WASAPI device did not open. PCM is dropped. Video is unchanged.";
        }

        if (usb)
        {
            return "USB 伴音（LPCM）已解开，但这台机器没有可用的播放设备（需要 libpulse-simple 或 libasound）。PCM 被丢掉，画面不受影响。USB LPCM was decoded, but no playback device is available (libpulse-simple or libasound). PCM is dropped. Video is unchanged.";
        }

        return OperatingSystem.IsWindows()
            ? "镜像伴音已解码，但打不开系统默认播放设备（WASAPI）。PCM 被丢掉，画面不受影响。Mirror audio was decoded, but the default WASAPI device did not open. PCM is dropped. Video is unchanged."
            : "镜像伴音已解码，但这台机器没有可用的播放设备（需要 libpulse-simple 或 libasound）。PCM 被丢掉，画面不受影响。Mirror audio was decoded, but no playback device is available (libpulse-simple or libasound). PCM is dropped. Video is unchanged.";
    }
}

internal static partial class WasapiNative
{
    internal const uint ClsctxAll = 23;

    internal static readonly Guid ClsidEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    internal static readonly Guid IidEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    internal static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    internal static readonly Guid IidRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    [LibraryImport("ole32")]
    internal static partial int CoInitializeEx(nint reserved, uint coinit);

    [LibraryImport("ole32")]
    internal static partial void CoUninitialize();

    [LibraryImport("ole32")]
    internal static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("ole32")]
    internal static partial void CoTaskMemFree(nint pointer);
}

/// <summary>
/// Shared-mode default render endpoint. The stream is 16-bit stereo or mono
/// at the AirPlay rate. <c>AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM</c> lets Windows
/// resample to the device mix format.
/// </summary>
internal sealed class WasapiPcmOutput : IPcmOutput
{
    private const uint AutoConvert = 0x80000000;

    private const uint DefaultQuality = 0x08000000;

    private readonly nint _client;
    private readonly nint _render;
    private readonly int _bufferFrames;
    private readonly bool _releaseCom;
    private int _channels;
    private int _rate;
    private int _disposed;

    private WasapiPcmOutput(nint client, nint render, int bufferFrames, bool releaseCom, int channels, int rate)
    {
        _client = client;
        _render = render;
        _bufferFrames = bufferFrames;
        _releaseCom = releaseCom;
        _channels = channels;
        _rate = rate;
    }

    public bool IsOpen => _disposed == 0;

    public string Name => "wasapi";

    public static WasapiPcmOutput? TryCreate(IAirPlayLog log, int sampleRate, int channels, bool usb)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var com = WasapiNative.CoInitializeEx(0, 0);
        var ownsCom = com == 0;
        if (com < 0 && com != unchecked((int)0x80010106))
        {
            log.Log(AirPlayLogLevel.Warning, "WASAPI CoInitializeEx 失败 0x" + unchecked((uint)com).ToString("X8") + ".");
            return null;
        }

        nint enumerator = 0;
        nint device = 0;
        nint client = 0;
        nint render = 0;
        try
        {
            var created = WasapiNative.CoCreateInstance(WasapiNative.ClsidEnumerator, 0, WasapiNative.ClsctxAll, WasapiNative.IidEnumerator, out enumerator);
            if (created < 0 || enumerator == 0)
            {
                log.Log(AirPlayLogLevel.Warning, "WASAPI 设备枚举没有打开 0x" + unchecked((uint)created).ToString("X8") + ".");
                return null;
            }

            if (GetDefault(enumerator, out device) < 0 || device == 0)
            {
                log.Log(AirPlayLogLevel.Warning, "没有默认的 WASAPI 播放设备。No default WASAPI render device.");
                return null;
            }

            if (Activate(device, out client) < 0 || client == 0)
            {
                log.Log(AirPlayLogLevel.Warning, "IAudioClient 没有激活。IAudioClient did not activate.");
                return null;
            }

            Span<byte> format = stackalloc byte[18];
            WritePcm(format, sampleRate, channels);
            var initialized = Initialize(client, format, 1_000_000);
            if (initialized < 0)
            {
                log.Log(AirPlayLogLevel.Warning, "WASAPI Initialize 失败 0x" + unchecked((uint)initialized).ToString("X8") + ".");
                return null;
            }

            if (GetBufferSize(client, out var frames) < 0 || frames <= 0)
            {
                return null;
            }

            if (GetService(client, out render) < 0 || render == 0)
            {
                return null;
            }

            if (Start(client) < 0)
            {
                Release(render);
                return null;
            }

            log.Log(
                AirPlayLogLevel.Information,
                usb
                    ? "USB 伴音使用系统默认播放设备（WASAPI，" + sampleRate + " Hz " + channels + " ch LPCM）。USB LPCM uses the default WASAPI render device."
                    : "镜像伴音使用系统默认播放设备（WASAPI）。Mirror audio uses the default WASAPI render device.");
            var output = new WasapiPcmOutput(client, render, frames, ownsCom, channels, sampleRate);
            client = 0;
            render = 0;
            ownsCom = false;
            return output;
        }
        finally
        {
            if (render != 0)
            {
                Release(render);
            }

            if (client != 0)
            {
                Release(client);
            }

            if (device != 0)
            {
                Release(device);
            }

            if (enumerator != 0)
            {
                Release(enumerator);
            }

            if (ownsCom)
            {
                WasapiNative.CoUninitialize();
            }
        }
    }

    public void Write(ReadOnlySpan<short> interleaved, int channels, int sampleRate)
    {
        if (_disposed != 0 || channels is < 1 or > 2 || interleaved.Length < channels)
        {
            return;
        }

        if (channels != _channels || sampleRate != _rate)
        {
            // The client was opened at 44.1 kHz stereo, and Windows converts.
            // A later description is still written as that stream.
            _channels = channels;
            _rate = sampleRate;
        }

        var frames = interleaved.Length / channels;
        var offset = 0;
        while (offset < frames && _disposed == 0)
        {
            if (GetPadding(_client, out var padding) < 0)
            {
                return;
            }

            var available = _bufferFrames - padding;
            if (available <= 0)
            {
                Thread.Sleep(4);
                continue;
            }

            var count = Math.Min(available, frames - offset);
            if (GetBuffer(_render, count, out var data) < 0 || data == 0)
            {
                return;
            }

            unsafe
            {
                var destination = new Span<byte>((void*)data, count * channels * 2);
                var source = MemoryMarshal.AsBytes(interleaved.Slice(offset * channels, count * channels));
                source.CopyTo(destination);
            }

            if (ReleaseBuffer(_render, count) < 0)
            {
                return;
            }

            offset += count;
        }
    }

    public void Flush()
    {
        if (_disposed != 0)
        {
            return;
        }

        Stop(_client);
        ResetClient(_client);
        Start(_client);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop(_client);
        Release(_render);
        Release(_client);
        if (_releaseCom)
        {
            WasapiNative.CoUninitialize();
        }
    }

    private static void WritePcm(Span<byte> format, int rate, int channels)
    {
        format.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(format, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(format[2..], (ushort)channels);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(format[4..], (uint)rate);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(format[8..], (uint)(rate * channels * 2));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(format[12..], (ushort)(channels * 2));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(format[14..], 16);
    }

    private static unsafe int GetDefault(nint enumerator, out nint device)
    {
        device = 0;
        var get = Vtable(enumerator, 4);
        var call = (delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)get;
        nint result;
        var status = call(enumerator, 0, 0, &result);
        device = result;
        return status;
    }

    private static unsafe int Activate(nint device, out nint client)
    {
        client = 0;
        var activate = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Vtable(device, 3);
        var iid = WasapiNative.IidAudioClient;
        nint result;
        var status = activate(device, &iid, WasapiNative.ClsctxAll, 0, &result);
        client = result;
        return status;
    }

    private static unsafe int Initialize(nint client, ReadOnlySpan<byte> format, long duration)
    {
        var init = (delegate* unmanaged[Stdcall]<nint, int, uint, long, long, nint, nint, int>)Vtable(client, 3);
        fixed (byte* pointer = format)
        {
            return init(client, 0, AutoConvert | DefaultQuality, duration, 0, (nint)pointer, 0);
        }
    }

    private static unsafe int GetBufferSize(nint client, out int frames)
    {
        frames = 0;
        var call = (delegate* unmanaged[Stdcall]<nint, int*, int>)Vtable(client, 4);
        int value;
        var status = call(client, &value);
        frames = value;
        return status;
    }

    private static unsafe int GetPadding(nint client, out int frames)
    {
        frames = 0;
        var call = (delegate* unmanaged[Stdcall]<nint, int*, int>)Vtable(client, 6);
        int value;
        var status = call(client, &value);
        frames = value;
        return status;
    }

    private static unsafe int GetService(nint client, out nint render)
    {
        render = 0;
        var call = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtable(client, 14);
        var iid = WasapiNative.IidRenderClient;
        nint result;
        var status = call(client, &iid, &result);
        render = result;
        return status;
    }

    private static unsafe int Start(nint client)
        => ((delegate* unmanaged[Stdcall]<nint, int>)Vtable(client, 10))(client);

    private static unsafe int Stop(nint client)
        => ((delegate* unmanaged[Stdcall]<nint, int>)Vtable(client, 11))(client);

    private static unsafe int ResetClient(nint client)
        => ((delegate* unmanaged[Stdcall]<nint, int>)Vtable(client, 12))(client);

    private static unsafe int GetBuffer(nint render, int frames, out nint data)
    {
        data = 0;
        var call = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vtable(render, 3);
        nint pointer;
        var status = call(render, frames, &pointer);
        data = pointer;
        return status;
    }

    private static unsafe int ReleaseBuffer(nint render, int frames)
        => ((delegate* unmanaged[Stdcall]<nint, int, uint, int>)Vtable(render, 4))(render, frames, 0);

    private static unsafe void Release(nint instance)
    {
        if (instance != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Vtable(instance, 2))(instance);
        }
    }

    private static nint Vtable(nint instance, int slot)
        => Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size);
}

internal static partial class PulseNative
{
    [LibraryImport("libpulse-simple.so.0")]
    internal static partial nint pa_simple_new(
        nint server,
        nint name,
        int direction,
        nint device,
        nint stream,
        in PaSampleSpec spec,
        nint map,
        nint attr,
        nint error);

    [LibraryImport("libpulse-simple.so.0")]
    internal static partial int pa_simple_write(nint simple, nint data, nuint bytes, nint error);

    [LibraryImport("libpulse-simple.so.0")]
    internal static partial int pa_simple_flush(nint simple, nint error);

    [LibraryImport("libpulse-simple.so.0")]
    internal static partial void pa_simple_free(nint simple);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PaSampleSpec
{
    public int Format;
    public uint Rate;
    public byte Channels;
}

internal sealed class PulsePcmOutput : IPcmOutput
{
    private readonly nint _simple;
    private int _disposed;

    private PulsePcmOutput(nint simple)
    {
        _simple = simple;
    }

    public bool IsOpen => _disposed == 0;

    public string Name => "pulse";

    public static unsafe PulsePcmOutput? TryCreate(IAirPlayLog log, int sampleRate, int channels, bool usb)
    {
        if (!NativeLibrary.TryLoad("libpulse-simple.so.0", out _) && !NativeLibrary.TryLoad("libpulse-simple.so", out _))
        {
            return null;
        }

        try
        {
            var spec = new PaSampleSpec { Format = 3, Rate = (uint)sampleRate, Channels = (byte)channels };
            var name = "AirPlayServer.NET"u8;
            var stream = "mirror"u8;
            fixed (byte* namePointer = name)
            fixed (byte* streamPointer = stream)
            {
                var simple = PulseNative.pa_simple_new(0, (nint)namePointer, 1, 0, (nint)streamPointer, spec, 0, 0, 0);
                if (simple == 0)
                {
                    return null;
                }

                log.Log(
                    AirPlayLogLevel.Information,
                    usb
                        ? "USB 伴音使用 PulseAudio 默认设备（" + sampleRate + " Hz " + channels + " ch LPCM）。USB LPCM uses the default PulseAudio device."
                        : "镜像伴音使用 PulseAudio 默认设备。Mirror audio uses the default PulseAudio device.");
                return new PulsePcmOutput(simple);
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    public void Write(ReadOnlySpan<short> interleaved, int channels, int sampleRate)
    {
        if (_disposed != 0 || interleaved.IsEmpty)
        {
            return;
        }

        unsafe
        {
            fixed (short* pointer = interleaved)
            {
                PulseNative.pa_simple_write(_simple, (nint)pointer, (nuint)(interleaved.Length * 2), 0);
            }
        }
    }

    public void Flush()
    {
        if (_disposed == 0)
        {
            PulseNative.pa_simple_flush(_simple, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            PulseNative.pa_simple_free(_simple);
        }
    }
}

internal static partial class AlsaNative
{
    [LibraryImport("libasound.so.2")]
    internal static partial int snd_pcm_open(out nint pcm, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int stream, int mode);

    [LibraryImport("libasound.so.2")]
    internal static partial int snd_pcm_set_params(nint pcm, int format, int access, uint channels, uint rate, int resample, uint latency);

    [LibraryImport("libasound.so.2")]
    internal static partial nint snd_pcm_writei(nint pcm, nint buffer, nuint frames);

    [LibraryImport("libasound.so.2")]
    internal static partial int snd_pcm_prepare(nint pcm);

    [LibraryImport("libasound.so.2")]
    internal static partial int snd_pcm_drop(nint pcm);

    [LibraryImport("libasound.so.2")]
    internal static partial int snd_pcm_close(nint pcm);
}

internal sealed class AlsaPcmOutput : IPcmOutput
{
    private readonly nint _pcm;
    private int _disposed;

    private AlsaPcmOutput(nint pcm)
    {
        _pcm = pcm;
    }

    public bool IsOpen => _disposed == 0;

    public string Name => "alsa";

    public static AlsaPcmOutput? TryCreate(IAirPlayLog log, int sampleRate, int channels, bool usb)
    {
        if (!NativeLibrary.TryLoad("libasound.so.2", out _))
        {
            return null;
        }

        try
        {
            if (AlsaNative.snd_pcm_open(out var pcm, "default", 0, 0) < 0 || pcm == 0)
            {
                return null;
            }

            if (AlsaNative.snd_pcm_set_params(pcm, 2, 3, (uint)channels, (uint)sampleRate, 1, 100_000) < 0)
            {
                AlsaNative.snd_pcm_close(pcm);
                return null;
            }

            log.Log(
                AirPlayLogLevel.Information,
                usb
                    ? "USB 伴音使用 ALSA 默认设备（" + sampleRate + " Hz " + channels + " ch LPCM）。USB LPCM uses the default ALSA device."
                    : "镜像伴音使用 ALSA 默认设备。Mirror audio uses the default ALSA device.");
            return new AlsaPcmOutput(pcm);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    public void Write(ReadOnlySpan<short> interleaved, int channels, int sampleRate)
    {
        if (_disposed != 0 || channels <= 0 || interleaved.IsEmpty)
        {
            return;
        }

        var frames = (nuint)(interleaved.Length / channels);
        unsafe
        {
            fixed (short* pointer = interleaved)
            {
                var written = AlsaNative.snd_pcm_writei(_pcm, (nint)pointer, frames);
                if (written == -32)
                {
                    AlsaNative.snd_pcm_prepare(_pcm);
                    AlsaNative.snd_pcm_writei(_pcm, (nint)pointer, frames);
                }
            }
        }
    }

    public void Flush()
    {
        if (_disposed == 0)
        {
            AlsaNative.snd_pcm_drop(_pcm);
            AlsaNative.snd_pcm_prepare(_pcm);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            AlsaNative.snd_pcm_close(_pcm);
        }
    }
}
