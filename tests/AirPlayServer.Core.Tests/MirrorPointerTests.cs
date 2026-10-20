using AirPlayServer.Core.Notify;

namespace AirPlayServer.Core.Tests;

public sealed class MirrorPointerTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 7, 40, 0, TimeSpan.Zero);

    [Fact]
    public void Map_keeps_move_phase_and_logical_points()
    {
        var mapped = MirrorClickMap.Map(
            MirrorPointerPhase.Move,
            MirrorPointerButton.Right,
            At,
            windowX: 10,
            windowY: 20,
            clientWidth: 100,
            clientHeight: 200,
            frameWidth: 50,
            frameHeight: 100,
            logicalWidth: 430,
            logicalHeight: 932);
        Assert.Equal(MirrorPointerPhase.Move, mapped.Phase);
        Assert.Equal(MirrorPointerButton.Right, mapped.Button);
        Assert.True(mapped.OnContent);
        Assert.Equal(5, mapped.ContentX);
        Assert.Equal(10, mapped.ContentY);
        Assert.Equal(5 * 430 / 50, mapped.LogicalX);
        Assert.Equal(10 * 932 / 100, mapped.LogicalY);
        Assert.Equal(430, mapped.LogicalWidth);
        Assert.Equal(932, mapped.LogicalHeight);

        var bar = MirrorClickMap.Map(
            MirrorPointerPhase.Move,
            MirrorPointerButton.Left,
            At,
            windowX: 0,
            windowY: 0,
            clientWidth: 200,
            clientHeight: 100,
            frameWidth: 50,
            frameHeight: 100,
            logicalWidth: 430,
            logicalHeight: 932);
        Assert.Equal(MirrorPointerPhase.Move, bar.Phase);
        Assert.False(bar.OnContent);
        Assert.Null(bar.ContentX);
        Assert.Null(bar.LogicalX);
    }

    [Fact]
    public void Drag_move_requires_a_held_button_and_a_new_pixel()
    {
        var drag = new MirrorPointerDrag();
        Assert.False(drag.TryMove(12, 20, At, onContent: true, out _));

        drag.NoteDown(MirrorPointerButton.Right, 10, 20, At);
        Assert.Equal(MirrorPointerButton.Right, drag.HeldButton);
        Assert.False(drag.TryMove(10, 20, At, onContent: true, out _));
        Assert.False(drag.TryMove(12, 22, At, onContent: false, out _));

        Assert.True(drag.TryMove(12, 20, At, onContent: true, out var button));
        Assert.Equal(MirrorPointerButton.Right, button);

        var tooSoon = At.Add(MirrorPointerDrag.MinInterval).Add(TimeSpan.FromTicks(-1));
        Assert.False(drag.TryMove(14, 20, tooSoon, onContent: true, out _));
        Assert.True(drag.TryMove(16, 20, At.Add(MirrorPointerDrag.MinInterval), onContent: true, out button));
        Assert.Equal(MirrorPointerButton.Right, button);

        drag.NoteUp(MirrorPointerButton.Left);
        Assert.Equal(MirrorPointerButton.Right, drag.HeldButton);
        drag.NoteUp(MirrorPointerButton.Right);
        Assert.Null(drag.HeldButton);
        Assert.False(drag.TryMove(30, 40, At.AddSeconds(1), onContent: true, out _));
    }

    [Fact]
    public void Drag_clear_stops_moves_until_the_next_press()
    {
        var drag = new MirrorPointerDrag();
        drag.NoteDown(MirrorPointerButton.Left, 1, 1, At);
        drag.Clear();
        Assert.Null(drag.HeldButton);
        Assert.False(drag.TryMove(4, 4, At.AddSeconds(1), onContent: true, out _));

        drag.NoteDown(MirrorPointerButton.Middle, 4, 4, At.AddSeconds(1));
        Assert.True(drag.TryMove(6, 4, At.AddSeconds(1), onContent: true, out var button));
        Assert.Equal(MirrorPointerButton.Middle, button);
    }

    [Fact]
    public void Drag_bars_do_not_consume_the_move_slot()
    {
        var drag = new MirrorPointerDrag();
        drag.NoteDown(MirrorPointerButton.Left, 0, 0, At);
        Assert.True(drag.TryMove(2, 0, At, onContent: true, out _));
        var later = At.Add(MirrorPointerDrag.MinInterval);
        Assert.False(drag.TryMove(8, 0, later, onContent: false, out _));
        Assert.True(drag.TryMove(8, 0, later, onContent: true, out var button));
        Assert.Equal(MirrorPointerButton.Left, button);
    }
}
