using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class SharedBgraPoolTests
{
    [Fact]
    public void Two_sessions_do_not_share_a_live_frame_and_idle_spares_are_capped()
    {
        var pool = BgraBufferPool.CreateShared();
        var first = new FrameSlot(pool);
        var second = new FrameSlot(pool);
        Assert.Same(first.Buffers, second.Buffers);

        var pixels = new byte[32 * 24 * 4];
        pixels[0] = 0x11;
        pixels[^1] = 0x21;
        first.Publish(32, 24, pixels, 128);
        pixels[0] = 0x22;
        pixels[^1] = 0x32;
        second.Publish(32, 24, pixels, 128);
        Assert.Equal(2, pool.Allocations);

        Assert.True(first.TryLease(out _, out _, out _, out var held, out _, out var lease));
        var snapshot = held.AsSpan(0, 4).ToArray();
        pixels[0] = 0x33;
        second.Publish(32, 24, pixels, 128);
        Assert.Equal(snapshot, held.AsSpan(0, 4).ToArray());
        Assert.Equal(0x11, held[0]);
        Assert.True(second.TryLease(out _, out _, out _, out var other, out _, out var otherLease));
        Assert.NotSame(held, other);
        Assert.Equal(0x33, other[0]);
        otherLease.Dispose();

        // Fill every idle array the pool will hand out. The leased frame stays put.
        var borrowed = new List<byte[]>();
        for (var i = 0; i < 8; i++)
        {
            var spare = pool.Rent(32 * 24 * 4);
            if (ReferenceEquals(spare, held))
            {
                lease.Dispose();
                Assert.Fail("A leased frame was rented by the other session.");
            }

            spare.AsSpan(0, 4).Fill(0xFF);
            borrowed.Add(spare);
        }

        Assert.Equal(0x11, held[0]);
        Assert.Equal(snapshot, held.AsSpan(0, 4).ToArray());
        lease.Dispose();
        foreach (var spare in borrowed)
        {
            pool.Return(spare);
        }

        var after = pool.Allocations;
        pixels[0] = 0x44;
        second.Publish(32, 24, pixels, 128);
        Assert.Equal(after, pool.Allocations);
        Assert.Equal(0x11, held[0]);
        Assert.True(second.TryRead(out _, out _, out _, out other, out _));
        Assert.Equal(0x44, other[0]);
        Assert.NotSame(held, other);
    }

    [Fact]
    public void In_place_publish_uses_one_buffer_per_session()
    {
        const int sessions = 8;
        const int width = 256;
        const int height = 256;
        const int bytes = width * height * 4;
        var pool = BgraBufferPool.CreateShared();
        var pixels = new byte[bytes];
        for (var i = 0; i < sessions; i++)
        {
            pixels[0] = (byte)(i + 1);
            var slot = new FrameSlot(pool);
            slot.Publish(width, height, pixels, width * 4);
            pixels[0] = (byte)(i + 40);
            slot.Publish(width, height, pixels, width * 4);
            Assert.True(slot.TryRead(out var readWidth, out var readHeight, out _, out var latest, out var generation));
            Assert.Equal(width, readWidth);
            Assert.Equal(height, readHeight);
            Assert.Equal((byte)(i + 40), latest[0]);
            Assert.Equal(2, generation);
        }

        Assert.Equal(sessions, pool.Allocations);
        Assert.Equal(0, pool.FreeCount);
        Assert.Equal((long)sessions * bytes, pool.CheckedOutBytes);
        // The previous per-session pool allocated a second array on the second
        // frame and kept it. One buffer per phone is half of that.
        var previousSteadyBytes = (long)sessions * 2 * bytes;
        Assert.True(pool.CheckedOutBytes + pool.RetainedFreeBytes <= previousSteadyBytes / 2);
    }

    [Fact]
    public void Idle_cap_is_half_the_simultaneous_checkout_not_four_per_session()
    {
        const int sessions = 8;
        const int extrasPerSession = 4;
        const int bytes = 64 * 1024;
        var pool = BgraBufferPool.CreateShared();
        var held = new byte[sessions * extrasPerSession][];
        for (var i = 0; i < held.Length; i++)
        {
            held[i] = pool.Rent(bytes);
            Assert.Equal(bytes, held[i].Length);
        }

        for (var i = 0; i < held.Length; i++)
        {
            pool.Return(held[i]);
        }

        var cap = Math.Max(BgraBufferPool.SharedSparesPerTier, held.Length / 2);
        Assert.Equal(cap, pool.FreeCount);
        Assert.Equal((long)cap * bytes, pool.RetainedFreeBytes);
        Assert.True(pool.FreeCount < held.Length);
        // Twenty phones at 1290×2794 (the size the decoder comments use).
        // Four retained full frames each is 1.07 GiB of idle BGRA. One displayed
        // frame per phone is a quarter of that. The literals fail if the product changes.
        const int phoneFrame = 1290 * 2794 * 4;
        Assert.Equal(14_417_040, phoneFrame);
        const int phones = 20;
        var oldFilled = (long)phones * 4 * phoneFrame;
        var displayedOnly = (long)phones * phoneFrame;
        Assert.Equal(1_153_363_200L, oldFilled);
        Assert.Equal(288_340_800L, displayedOnly);
        Assert.Equal(oldFilled, displayedOnly * 4);
    }

    [Fact]
    public void Same_size_tier_serves_another_session_after_return()
    {
        var pool = BgraBufferPool.CreateShared();
        var buffer = pool.Rent(14_417_040);
        Assert.Equal(14_417_040, buffer.Length);
        Assert.Equal(BgraBufferPool.TierSlot(8 * 1024 * 1024), BgraBufferPool.TierSlot(buffer.Length));
        Assert.Equal(BgraBufferPool.TierSlot(buffer.Length), BgraBufferPool.TierSlot(12_000_000));
        pool.Return(buffer);
        var tiny = pool.Rent(16);
        Assert.NotSame(buffer, tiny);
        Assert.True(tiny.Length < BgraBufferPool.TierMinimum);
        pool.Return(tiny);
        var again = pool.Rent(12_000_000);
        Assert.Same(buffer, again);
        pool.Return(again);
    }
}
