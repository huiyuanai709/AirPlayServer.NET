namespace AirPlayServer.Core.Video;

/// <summary>
/// Grow-only BGRA arrays for the live mirror. A published picture checks a
/// buffer out; returning it makes that same array available for a later frame
/// instead of allocating another ~14 MB at 1290×2794.
/// </summary>
internal sealed class BgraBufferPool
{
    private readonly object _gate = new();
    private readonly byte[]?[] _free = new byte[]?[4];
    private int _allocations;

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

    internal byte[] Rent(int bytes)
    {
        if (bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        lock (_gate)
        {
            var best = -1;
            var bestLength = int.MaxValue;
            for (var i = 0; i < _free.Length; i++)
            {
                var item = _free[i];
                if (item is not null && item.Length >= bytes && item.Length < bestLength)
                {
                    best = i;
                    bestLength = item.Length;
                }
            }

            if (best >= 0)
            {
                var chosen = _free[best]!;
                _free[best] = null;
                return chosen;
            }

            _allocations++;
            return new byte[bytes];
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
            var empty = -1;
            var smallest = -1;
            for (var i = 0; i < _free.Length; i++)
            {
                var item = _free[i];
                if (item is null)
                {
                    empty = i;
                    break;
                }

                if (smallest < 0 || item.Length < _free[smallest]!.Length)
                {
                    smallest = i;
                }
            }

            if (empty >= 0)
            {
                _free[empty] = buffer;
                return;
            }

            if (smallest >= 0 && buffer.Length > _free[smallest]!.Length)
            {
                _free[smallest] = buffer;
            }
        }
    }
}
