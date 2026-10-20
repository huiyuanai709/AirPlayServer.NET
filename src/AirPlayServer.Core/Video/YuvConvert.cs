using System.Runtime.CompilerServices;

namespace AirPlayServer.Core.Video;

/// <summary>
/// BT.601 / BT.709 tables for the integer NV12 and YUV 4:2:0 conversion.
/// Built from <see cref="Nv12Color"/> so a bulk copy matches
/// <see cref="Nv12Color.Convert"/> byte for byte. Four color spaces, looked up
/// once per picture.
/// </summary>
internal sealed class YuvConvertTable
{
    internal readonly int[] Luma = new int[256];

    internal readonly int[] CbB = new int[256];

    internal readonly int[] CbG = new int[256];

    internal readonly int[] CrR = new int[256];

    internal readonly int[] CrG = new int[256];

    internal readonly byte[] Bytes;

    internal readonly int Bias;

    private static readonly YuvConvertTable[] Cache =
    [
        new(YuvMatrix.Bt709, YuvRange.Limited),
        new(YuvMatrix.Bt709, YuvRange.Full),
        new(YuvMatrix.Bt601, YuvRange.Limited),
        new(YuvMatrix.Bt601, YuvRange.Full),
    ];

    private YuvConvertTable(YuvMatrix matrix, YuvRange range)
    {
        Nv12Color.Coefficients(matrix, out var crToR, out var cbToG, out var crToG, out var cbToB);
        for (var value = 0; value < 256; value++)
        {
            Luma[value] = Nv12Color.NormalizeLuma(value, range);
            var chroma = Nv12Color.NormalizeChroma(value, range);
            CbB[value] = Nv12Color.Scale(cbToB, chroma);
            CbG[value] = Nv12Color.Scale(cbToG, chroma);
            CrR[value] = Nv12Color.Scale(crToR, chroma);
            CrG[value] = Nv12Color.Scale(crToG, chroma);
        }

        SpanMinMax(Luma, out var minLuma, out var maxLuma);
        SpanMinMax(CbB, out var minCbB, out var maxCbB);
        SpanMinMax(CbG, out var minCbG, out var maxCbG);
        SpanMinMax(CrR, out var minCrR, out var maxCrR);
        SpanMinMax(CrG, out var minCrG, out var maxCrG);
        var min = Math.Min(minLuma + minCbB, Math.Min(minLuma + minCrR, minLuma - maxCbG - maxCrG));
        var max = Math.Max(maxLuma + maxCbB, Math.Max(maxLuma + maxCrR, maxLuma - minCbG - minCrG));
        Bias = -min;
        Bytes = new byte[max - min + 1];
        for (var q = min; q <= max; q++)
        {
            Bytes[q + Bias] = Nv12Color.ToByte(q);
        }
    }

    internal static YuvConvertTable For(YuvColorSpace color)
        => Cache[((int)color.Matrix << 1) | (int)color.Range];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte Blue(int y, int cb) => Bytes[Luma[y] + CbB[cb] + Bias];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte Green(int y, int cb, int cr) => Bytes[Luma[y] - CbG[cb] - CrG[cr] + Bias];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte Red(int y, int cr) => Bytes[Luma[y] + CrR[cr] + Bias];

    private static void SpanMinMax(int[] values, out int min, out int max)
    {
        min = values[0];
        max = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i];
            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }
        }
    }
}

/// <summary>
/// Tight BGRA from 8-bit YUV 4:2:0. Chroma is shared by the even pixel and the
/// pixel to its right. One phone-sized frame is millions of pixels; the tables
/// replace the per-pixel divisions.
/// </summary>
internal static class YuvConvert
{
    /// <summary>
    /// Extra row workers for one large picture. A second session that arrives
    /// while they are busy converts on its own thread, so phones do not queue
    /// behind one global lock.
    /// </summary>
    private static readonly int ExtraWorkers = Math.Clamp(Environment.ProcessorCount - 1, 0, 3);

    private static readonly object ParallelGate = new();

    private static readonly PlaneSlot[] Slots = new PlaneSlot[3];

    private static Plane _shared;

    private static int _workers;

    internal static unsafe void CopyNv12(
        byte* yPlane,
        int yStride,
        byte* uvPlane,
        int uvStride,
        int width,
        int height,
        YuvColorSpace color,
        byte* destination)
        => CopyPlanes(yPlane, yStride, uvPlane, uvStride, null, 0, width, height, color, nv12: true, swapUv: false, destination);

    internal static unsafe void CopyPlanes(
        byte* yPlane,
        int yStride,
        byte* uPlane,
        int uStride,
        byte* vPlane,
        int vStride,
        int width,
        int height,
        YuvColorSpace color,
        bool nv12,
        bool swapUv,
        byte* destination)
    {
        var table = YuvConvertTable.For(color);
        fixed (int* lumaQ = table.Luma)
        fixed (int* cbB = table.CbB)
        fixed (int* cbG = table.CbG)
        fixed (int* crR = table.CrR)
        fixed (int* crG = table.CrG)
        fixed (byte* map = table.Bytes)
        {
            var bias = table.Bias;
            var pixels = (long)width * height;
            if (ExtraWorkers > 0 && height >= 32 && pixels >= 1280L * 720 && Monitor.TryEnter(ParallelGate))
            {
                try
                {
                    EnsureWorkers();
                    Fill(ref _shared, yPlane, yStride, uPlane, uStride, vPlane, vStride, width, height, nv12, swapUv, lumaQ, cbB, cbG, crR, crG, map, bias, destination);
                    var bands = _workers + 1;
                    var span = height / bands;
                    for (var i = 0; i < _workers; i++)
                    {
                        var slot = Slots[i];
                        slot.From = i * span;
                        slot.To = (i + 1) * span;
                        slot.Done.Reset();
                        slot.HasWork.Set();
                    }

                    ConvertRange(ref _shared, _workers * span, height);
                    Exception? workerError = null;
                    for (var i = 0; i < _workers; i++)
                    {
                        var slot = Slots[i];
                        slot.Done.Wait();
                        workerError ??= slot.Error;
                    }

                    if (workerError is not null)
                    {
                        throw workerError;
                    }

                    return;
                }
                finally
                {
                    Monitor.Exit(ParallelGate);
                }
            }

            var single = new Plane();
            Fill(ref single, yPlane, yStride, uPlane, uStride, vPlane, vStride, width, height, nv12, swapUv, lumaQ, cbB, cbG, crR, crG, map, bias, destination);
            ConvertRange(ref single, 0, height);
        }
    }

    private static unsafe void Fill(
        ref Plane plane,
        byte* yPlane,
        int yStride,
        byte* uPlane,
        int uStride,
        byte* vPlane,
        int vStride,
        int width,
        int height,
        bool nv12,
        bool swapUv,
        int* lumaQ,
        int* cbB,
        int* cbG,
        int* crR,
        int* crG,
        byte* map,
        int bias,
        byte* destination)
    {
        plane.Y = (nint)yPlane;
        plane.U = (nint)uPlane;
        plane.V = (nint)vPlane;
        plane.Destination = (nint)destination;
        plane.Map = (nint)map;
        plane.LumaQ = (nint)lumaQ;
        plane.CbB = (nint)cbB;
        plane.CbG = (nint)cbG;
        plane.CrR = (nint)crR;
        plane.CrG = (nint)crG;
        plane.YStride = yStride;
        plane.UStride = uStride;
        plane.VStride = vStride;
        plane.Width = width;
        plane.Height = height;
        plane.Bias = bias;
        plane.Nv12 = nv12;
        plane.SwapUv = swapUv;
    }

    private static void EnsureWorkers()
    {
        if (_workers != 0)
        {
            return;
        }

        _workers = ExtraWorkers;
        for (var i = 0; i < _workers; i++)
        {
            var slot = new PlaneSlot();
            Slots[i] = slot;
            var thread = new Thread(slot.Run)
            {
                IsBackground = true,
                Name = "AirPlay YUV",
            };
            thread.Start();
        }
    }

    private static unsafe void ConvertRange(ref Plane plane, int from, int to)
    {
        var yPlane = (byte*)plane.Y;
        var uPlane = (byte*)plane.U;
        var vPlane = (byte*)plane.V;
        var destination = (byte*)plane.Destination;
        var lumaQ = (int*)plane.LumaQ;
        var cbB = (int*)plane.CbB;
        var cbG = (int*)plane.CbG;
        var crR = (int*)plane.CrR;
        var crG = (int*)plane.CrG;
        var map = (byte*)plane.Map;
        var width = plane.Width;
        var height = plane.Height;
        var bias = plane.Bias;
        var nv12 = plane.Nv12;
        var swapUv = plane.SwapUv;
        var chromaHeight = height / 2;
        var rowBytes = (long)width * 4;
        for (var y = from; y < to; y++)
        {
            var luma = Line(yPlane, plane.YStride, y, height);
            var chromaY = y >> 1;
            var u = Line(uPlane, plane.UStride, chromaY, chromaHeight);
            var v = nv12 ? u : Line(vPlane, plane.VStride, chromaY, chromaHeight);
            ConvertRow(luma, u, v, width, nv12, swapUv, lumaQ, cbB, cbG, crR, crG, map, bias, destination + (y * rowBytes));
        }
    }

    private struct Plane
    {
        public nint Y;
        public nint U;
        public nint V;
        public nint Destination;
        public nint Map;
        public nint LumaQ;
        public nint CbB;
        public nint CbG;
        public nint CrR;
        public nint CrG;
        public int YStride;
        public int UStride;
        public int VStride;
        public int Width;
        public int Height;
        public int Bias;
        public bool Nv12;
        public bool SwapUv;
    }

    private sealed class PlaneSlot
    {
        public readonly ManualResetEventSlim HasWork = new(false);
        public readonly ManualResetEventSlim Done = new(false);
        public int From;
        public int To;

        public Exception? Error { get; private set; }

        public void Run()
        {
            while (true)
            {
                HasWork.Wait();
                HasWork.Reset();
                try
                {
                    ConvertRange(ref _shared, From, To);
                    Error = null;
                }
                catch (Exception ex)
                {
                    // The caller is blocked on Done. Leave the picture incomplete
                    // and surface the exception on the decode thread.
                    Error = ex;
                }
                finally
                {
                    Done.Set();
                }
            }
        }
    }

    private static unsafe void ConvertRow(
        byte* luma,
        byte* u,
        byte* v,
        int width,
        bool nv12,
        bool swapUv,
        int* lumaQ,
        int* cbB,
        int* cbG,
        int* crR,
        int* crG,
        byte* map,
        int bias,
        byte* destination)
    {
        var x = 0;
        if (BitConverter.IsLittleEndian)
        {
            for (; x + 1 < width; x += 2)
            {
                LoadChroma(u, v, x, nv12, swapUv, out var cb, out var cr);
                var cbb = cbB[cb];
                var cbg = cbG[cb];
                var crr = crR[cr];
                var crg = crG[cr];
                Store(destination, lumaQ[luma[x]], cbb, cbg, crr, crg, map, bias);
                Store(destination + 4, lumaQ[luma[x + 1]], cbb, cbg, crr, crg, map, bias);
                destination += 8;
            }

            if (x < width)
            {
                LoadChroma(u, v, x, nv12, swapUv, out var cb, out var cr);
                Store(destination, lumaQ[luma[x]], cbB[cb], cbG[cb], crR[cr], crG[cr], map, bias);
            }

            return;
        }

        for (; x < width; x++)
        {
            LoadChroma(u, v, x, nv12, swapUv, out var cb, out var cr);
            var l = lumaQ[luma[x]];
            destination[0] = map[l + cbB[cb] + bias];
            destination[1] = map[l - cbG[cb] - crG[cr] + bias];
            destination[2] = map[l + crR[cr] + bias];
            destination[3] = 255;
            destination += 4;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void LoadChroma(byte* u, byte* v, int x, bool nv12, bool swapUv, out byte cb, out byte cr)
    {
        if (nv12)
        {
            var pair = x & ~1;
            cb = u[pair];
            cr = u[pair + 1];
        }
        else
        {
            var chromaX = x >> 1;
            cb = u[chromaX];
            cr = v[chromaX];
        }

        if (swapUv)
        {
            (cb, cr) = (cr, cb);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Store(byte* destination, int luma, int cbB, int cbG, int crR, int crG, byte* map, int bias)
    {
        var pixel = (uint)map[luma + cbB + bias]
            | ((uint)map[luma - cbG - crG + bias] << 8)
            | ((uint)map[luma + crR + bias] << 16)
            | 0xFF000000u;
        Unsafe.WriteUnaligned(destination, pixel);
    }

    /// <summary>
    /// Matches <see cref="YuvPlanarColor"/> negative-stride addressing. Positive
    /// stride is the path a mirror frame actually takes.
    /// </summary>
    private static unsafe byte* Line(byte* data, int stride, int y, int height)
    {
        if (stride >= 0)
        {
            return data + ((long)y * stride);
        }

        var row0 = data + ((long)(height - 1) * stride);
        return row0 + ((long)y * -stride);
    }
}
