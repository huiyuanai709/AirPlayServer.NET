using System.Globalization;

namespace AirPlayServer.Core.Audio;

/// <summary>
/// AirPlay <c>SET_PARAMETER</c> volume is decibels, clamped to [-144, 0].
/// Playback gain follows the upstream player: -30 dB and below is silence,
/// 0 dB is unity, and the span between them is linear. That normalized gain
/// scales each PCM sample. <c>GET_PARAMETER</c> reports the stored decibel
/// value, not the normalized gain.
/// </summary>
internal static class AirPlayVolume
{
    public const float MinimumDb = -144f;

    public const float MaximumDb = 0f;

    /// <summary>Upstream playback mute threshold. Below this the gain is zero.</summary>
    public const float MuteDb = -30f;

    public const int SdlMax = 128;

    public static float ClampDb(float db)
    {
        if (float.IsNaN(db) || db < MinimumDb)
        {
            return MinimumDb;
        }

        if (db > MaximumDb)
        {
            return MaximumDb;
        }

        return db;
    }

    /// <summary>0 is silence, <see cref="SdlMax"/> is unity. Matches the upstream mixer scale.</summary>
    public static int ToSdl(float db)
    {
        db = ClampDb(db);
        if (db <= MuteDb)
        {
            return 0;
        }

        if (db >= MaximumDb)
        {
            return SdlMax;
        }

        var normalized = (db - MuteDb) / (MaximumDb - MuteDb);
        var volume = (int)(normalized * SdlMax);
        if (volume < 0)
        {
            return 0;
        }

        return volume > SdlMax ? SdlMax : volume;
    }

    public static void Apply(Span<short> samples, int sdlVolume)
    {
        if (sdlVolume >= SdlMax)
        {
            return;
        }

        if (sdlVolume <= 0)
        {
            samples.Clear();
            return;
        }

        for (var i = 0; i < samples.Length; i++)
        {
            var sample = ((int)samples[i] * sdlVolume) / SdlMax;
            if (sample > short.MaxValue)
            {
                sample = short.MaxValue;
            }
            else if (sample < short.MinValue)
            {
                sample = short.MinValue;
            }

            samples[i] = (short)sample;
        }
    }

    public static string FormatParameter(float db)
    {
        var text = ClampDb(db).ToString("0.0######", CultureInfo.InvariantCulture);
        return "volume: " + text + "\r\n";
    }

    public static bool BodyRequestsVolume(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<byte> needle = "volume"u8;
        for (var i = 0; i + needle.Length <= body.Length; i++)
        {
            if (body.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryParseSetVolume(ReadOnlySpan<byte> body, out float db)
    {
        db = 0;
        if (body.IsEmpty)
        {
            return false;
        }

        var text = System.Text.Encoding.ASCII.GetString(body);
        var index = text.IndexOf("volume:", StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        var start = index + "volume:".Length;
        while (start < text.Length && text[start] is ' ' or '\t')
        {
            start++;
        }

        var end = start;
        while (end < text.Length && text[end] is not '\r' and not '\n')
        {
            end++;
        }

        if (start == end)
        {
            return false;
        }

        if (!float.TryParse(text.AsSpan(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        db = ClampDb(parsed);
        return true;
    }

    public static bool TryReadFlushSequence(string? rtpInfo, out int nextSequence)
    {
        nextSequence = -1;
        if (string.IsNullOrEmpty(rtpInfo))
        {
            return false;
        }

        var index = rtpInfo.IndexOf("seq=", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var start = index + 4;
        var end = start;
        while (end < rtpInfo.Length && rtpInfo[end] is >= '0' and <= '9')
        {
            end++;
        }

        if (end == start)
        {
            return false;
        }

        if (!int.TryParse(rtpInfo.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || sequence is < 0 or > 65535)
        {
            return false;
        }

        nextSequence = sequence;
        return true;
    }
}
