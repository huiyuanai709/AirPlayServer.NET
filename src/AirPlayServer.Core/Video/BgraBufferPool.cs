namespace AirPlayServer.Core.Video;

/// <summary>
/// BGRA arrays for decoded mirror frames. A private pool keeps a few exact-size
/// spares for one <see cref="FrameSlot"/>. <see cref="CreateShared"/> is the
/// receiver-wide pool: returned arrays are bucketed by size (powers of two, 累乘档)
/// and idle spares are capped per bucket, so twenty phones do not each retain
/// their own spare full-frame buffer.
/// A rented array belongs to one caller until <see cref="Return"/>. Two sessions
/// never receive the same array at the same time.
/// </summary>
internal sealed class BgraBufferPool
{
    /// <summary>Smallest power-of-two bucket. Smaller arrays share the bucket below this.</summary>
    internal const int TierMinimum = 4096;

    /// <summary>
    /// Idle arrays kept in a bucket even before checkout peak is known.
    /// The real cap is <c>max(this, peakCheckout / 2)</c> so a burst of returns
    /// can be rented again without allocating every frame, without keeping four
    /// full frames per phone.
    /// </summary>
    internal const int SharedSparesPerTier = 2;

    private const int TierCount = 16;

    private readonly bool _shared;
    private readonly object _gate = new();
    private readonly byte[]?[] _exact = new byte[]?[4];
    private readonly List<byte[]>[]? _tiers;
    private readonly int[]? _tierOut;
    private readonly int[]? _tierPeak;
    private int _allocations;
    private int _checkedOut;
    private long _checkedOutBytes;
    private long _freeBytes;

    internal BgraBufferPool()
        : this(shared: false)
    {
    }

    private BgraBufferPool(bool shared)
    {
        _shared = shared;
        if (!shared)
        {
            return;
        }

        _tiers = new List<byte[]>[TierCount];
        _tierOut = new int[TierCount];
        _tierPeak = new int[TierCount];
        for (var i = 0; i < TierCount; i++)
        {
            _tiers[i] = [];
        }
    }

    /// <summary>One pool for every mirror session on this receiver.</summary>
    internal static BgraBufferPool CreateShared() => new(shared: true);

    internal bool IsShared => _shared;

    internal int Allocations
    {
        get
        {
            lock (_gate)
            {
                return _allocations;
            }
        }
    }

    internal int CheckedOut
    {
        get
        {
            lock (_gate)
            {
                return _checkedOut;
            }
        }
    }

    internal long CheckedOutBytes
    {
        get
        {
            lock (_gate)
            {
                return _checkedOutBytes;
            }
        }
    }

    internal long RetainedFreeBytes
    {
        get
        {
            lock (_gate)
            {
                return _freeBytes;
            }
        }
    }

    internal int FreeCount
    {
        get
        {
            lock (_gate)
            {
                if (!_shared || _tiers is null)
                {
                    var count = 0;
                    for (var i = 0; i < _exact.Length; i++)
                    {
                        if (_exact[i] is not null)
                        {
                            count++;
                        }
                    }

                    return count;
                }

                var total = 0;
                for (var i = 0; i < _tiers.Length; i++)
                {
                    total += _tiers[i].Count;
                }

                return total;
            }
        }
    }

    internal byte[] Rent(int bytes)
    {
        if (bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        lock (_gate)
        {
            if (_shared)
            {
                var reused = TakeShared(bytes);
                if (reused is not null)
                {
                    NoteCheckedOut(reused.Length, +1);
                    return reused;
                }
            }
            else
            {
                var best = -1;
                var bestLength = int.MaxValue;
                for (var i = 0; i < _exact.Length; i++)
                {
                    var item = _exact[i];
                    if (item is not null && item.Length >= bytes && item.Length < bestLength)
                    {
                        best = i;
                        bestLength = item.Length;
                    }
                }

                if (best >= 0)
                {
                    var chosen = _exact[best]!;
                    _exact[best] = null;
                    _freeBytes -= chosen.Length;
                    NoteCheckedOut(chosen.Length, +1);
                    return chosen;
                }
            }

            _allocations++;
            var allocated = new byte[bytes];
            NoteCheckedOut(allocated.Length, +1);
            return allocated;
        }
    }

    internal void Return(byte[]? buffer)
    {
        if (buffer is null || buffer.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            NoteCheckedOut(buffer.Length, -1);
            if (_shared)
            {
                ReturnShared(buffer);
                return;
            }

            var empty = -1;
            var smallest = -1;
            for (var i = 0; i < _exact.Length; i++)
            {
                var item = _exact[i];
                if (item is null)
                {
                    empty = i;
                    break;
                }

                if (smallest < 0 || item.Length < _exact[smallest]!.Length)
                {
                    smallest = i;
                }
            }

            if (empty >= 0)
            {
                _exact[empty] = buffer;
                _freeBytes += buffer.Length;
                return;
            }

            if (smallest >= 0 && buffer.Length > _exact[smallest]!.Length)
            {
                _freeBytes -= _exact[smallest]!.Length;
                _exact[smallest] = buffer;
                _freeBytes += buffer.Length;
            }
        }
    }

    private byte[]? TakeShared(int bytes)
    {
        var tiers = _tiers;
        if (tiers is null)
        {
            return null;
        }

        // Stay in this size class. A 16-byte rent must not be handed a 14 MB frame.
        var slot = TierSlot(bytes);
        var list = tiers[slot];
        byte[]? best = null;
        var bestIndex = -1;
        var bestLength = int.MaxValue;
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (item.Length >= bytes && item.Length < bestLength)
            {
                best = item;
                bestLength = item.Length;
                bestIndex = i;
            }
        }

        if (best is null)
        {
            return null;
        }

        var home = list;
        var last = home.Count - 1;
        home[bestIndex] = home[last];
        home.RemoveAt(last);
        _freeBytes -= best.Length;
        return best;
    }

    private void ReturnShared(byte[] buffer)
    {
        var tiers = _tiers;
        var peaks = _tierPeak;
        if (tiers is null || peaks is null)
        {
            return;
        }

        var slot = TierSlot(buffer.Length);
        var list = tiers[slot];
        var cap = Math.Max(SharedSparesPerTier, peaks[slot] / 2);
        if (list.Count < cap)
        {
            list.Add(buffer);
            _freeBytes += buffer.Length;
            return;
        }

        var smallest = 0;
        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].Length < list[smallest].Length)
            {
                smallest = i;
            }
        }

        if (list.Count > 0 && buffer.Length > list[smallest].Length)
        {
            _freeBytes -= list[smallest].Length;
            list[smallest] = buffer;
            _freeBytes += buffer.Length;
        }
    }

    private void NoteCheckedOut(int length, int direction)
    {
        _checkedOut += direction;
        if (_checkedOut < 0)
        {
            _checkedOut = 0;
        }

        _checkedOutBytes += (long)direction * length;
        if (_checkedOutBytes < 0)
        {
            _checkedOutBytes = 0;
        }

        if (!_shared || _tierOut is null || _tierPeak is null)
        {
            return;
        }

        var slot = TierSlot(length);
        _tierOut[slot] += direction;
        if (_tierOut[slot] < 0)
        {
            _tierOut[slot] = 0;
        }

        if (_tierOut[slot] > _tierPeak[slot])
        {
            _tierPeak[slot] = _tierOut[slot];
        }
    }

    internal static int TierSlot(int length)
    {
        if (length < TierMinimum)
        {
            return 0;
        }

        var slot = 1;
        while (slot < TierCount - 1 && TierFloor(slot + 1) <= length)
        {
            slot++;
        }

        return slot;
    }

    internal static int TierFloor(int slot)
        => slot <= 0 ? 0 : TierMinimum << (slot - 1);
}
