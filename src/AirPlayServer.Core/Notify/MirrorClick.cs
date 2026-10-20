using System.Text.Json.Serialization;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Notify;

public enum MirrorPointerPhase
{
    Down = 0,
    Up = 1,
}

public enum MirrorPointerButton
{
    Left = 0,
    Right = 1,
    Middle = 2,
    X1 = 3,
    X2 = 4,
}

/// <summary>
/// One press or release on a mirror window. Window coordinates are device
/// pixels of the client, origin at the top left, Y down. Content coordinates
/// are decoded-frame pixels after letterbox. Bars are not content.
/// Logical coordinates exist only when a logical screen size was configured
/// and the point is on the picture: <c>logical = content * logicalSize / frameSize</c>,
/// integer truncation, each axis on its own. This event is not sent to the phone.
/// </summary>
public sealed class MirrorPointerGesture
{
    public required MirrorPointerPhase Phase { get; init; }

    public required MirrorPointerButton Button { get; init; }

    public required DateTimeOffset At { get; init; }

    public int WindowX { get; init; }

    public int WindowY { get; init; }

    public bool OnContent { get; init; }

    public int? ContentX { get; init; }

    public int? ContentY { get; init; }

    public int? ContentWidth { get; init; }

    public int? ContentHeight { get; init; }

    public int? LogicalX { get; init; }

    public int? LogicalY { get; init; }

    public int? LogicalWidth { get; init; }

    public int? LogicalHeight { get; init; }
}

public static class MirrorClickMap
{
    public static MirrorPointerGesture Map(
        MirrorPointerPhase phase,
        MirrorPointerButton button,
        DateTimeOffset at,
        int windowX,
        int windowY,
        int clientWidth,
        int clientHeight,
        int frameWidth,
        int frameHeight,
        int logicalWidth,
        int logicalHeight)
    {
        int? contentX = null;
        int? contentY = null;
        int? contentWidth = null;
        int? contentHeight = null;
        int? logicalX = null;
        int? logicalY = null;
        int? mappedLogicalWidth = null;
        int? mappedLogicalHeight = null;
        var onContent = false;
        if (frameWidth > 0 && frameHeight > 0 && clientWidth > 0 && clientHeight > 0)
        {
            contentWidth = frameWidth;
            contentHeight = frameHeight;
            var destination = FrameCoordinateMap.Letterbox(clientWidth, clientHeight, frameWidth, frameHeight);
            if (FrameCoordinateMap.TryMapClientToFrame(windowX, windowY, destination, frameWidth, frameHeight, out var frameX, out var frameY))
            {
                onContent = true;
                contentX = frameX;
                contentY = frameY;
                if (logicalWidth > 0 && logicalHeight > 0
                    && FrameCoordinateMap.TryMapFrameToScreen(frameX, frameY, frameWidth, frameHeight, logicalWidth, logicalHeight, out var screenX, out var screenY))
                {
                    logicalX = screenX;
                    logicalY = screenY;
                    mappedLogicalWidth = logicalWidth;
                    mappedLogicalHeight = logicalHeight;
                }
            }
        }

        if (!onContent && logicalWidth > 0 && logicalHeight > 0)
        {
            mappedLogicalWidth = logicalWidth;
            mappedLogicalHeight = logicalHeight;
        }

        return new MirrorPointerGesture
        {
            Phase = phase,
            Button = button,
            At = at,
            WindowX = windowX,
            WindowY = windowY,
            OnContent = onContent,
            ContentX = contentX,
            ContentY = contentY,
            ContentWidth = contentWidth,
            ContentHeight = contentHeight,
            LogicalX = logicalX,
            LogicalY = logicalY,
            LogicalWidth = mappedLogicalWidth,
            LogicalHeight = mappedLogicalHeight,
        };
    }
}

public sealed class MirrorClickEventArgs : EventArgs
{
    public string? SessionId { get; init; }

    public string? DeviceName { get; init; }

    public string? SourceIp { get; init; }

    public required MirrorPointerGesture Gesture { get; init; }
}

/// <summary>
/// In-process publish point for mirror-window pointer events. Subscribers,
/// including the webhook dispatcher, attach here. Nothing in this type names
/// a downstream product.
/// </summary>
public sealed class MirrorClickHub
{
    public event EventHandler<MirrorClickEventArgs>? Published;

    public void Publish(MirrorClickEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Published?.Invoke(this, args);
    }
}

public sealed class MirrorClickBody
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = "mirror.click";

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "";

    [JsonPropertyName("at")]
    public string At { get; set; } = "";

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("device")]
    public string Device { get; set; } = "";

    [JsonPropertyName("source_ip")]
    public string? SourceIp { get; set; }

    [JsonPropertyName("button")]
    public string Button { get; set; } = "";

    [JsonPropertyName("window_x")]
    public int WindowX { get; set; }

    [JsonPropertyName("window_y")]
    public int WindowY { get; set; }

    [JsonPropertyName("on_content")]
    public bool OnContent { get; set; }

    [JsonPropertyName("content_x")]
    public int? ContentX { get; set; }

    [JsonPropertyName("content_y")]
    public int? ContentY { get; set; }

    [JsonPropertyName("content_width")]
    public int? ContentWidth { get; set; }

    [JsonPropertyName("content_height")]
    public int? ContentHeight { get; set; }

    [JsonPropertyName("logical_x")]
    public int? LogicalX { get; set; }

    [JsonPropertyName("logical_y")]
    public int? LogicalY { get; set; }

    [JsonPropertyName("logical_width")]
    public int? LogicalWidth { get; set; }

    [JsonPropertyName("logical_height")]
    public int? LogicalHeight { get; set; }
}
