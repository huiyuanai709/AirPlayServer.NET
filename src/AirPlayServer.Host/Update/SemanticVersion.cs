using System.Globalization;

namespace AirPlayServer.Host.Update;

/// <summary>
/// Stable <c>vX.Y.Z</c> tags, plus an optional semver pre-release.
/// Build metadata after <c>+</c> is ignored for precedence.
/// </summary>
internal readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version) =>
        TryParse(text.AsSpan(), out version);

    public static bool TryParse(ReadOnlySpan<char> text, out SemanticVersion version)
    {
        version = default;
        text = text.Trim();
        if (text.IsEmpty)
        {
            return false;
        }

        if (text[0] is 'v' or 'V')
        {
            text = text[1..];
            if (text.IsEmpty)
            {
                return false;
            }
        }

        var plus = text.IndexOf('+');
        if (plus == 0)
        {
            return false;
        }

        if (plus > 0)
        {
            text = text[..plus];
        }

        string? preRelease = null;
        var dash = text.IndexOf('-');
        var core = text;
        if (dash >= 0)
        {
            if (dash == 0 || dash == text.Length - 1)
            {
                return false;
            }

            core = text[..dash];
            preRelease = text[(dash + 1)..].ToString();
            if (!IsValidPreRelease(preRelease))
            {
                return false;
            }
        }

        Span<Range> parts = stackalloc Range[5];
        var count = core.Split(parts, '.', StringSplitOptions.None);
        if (count is < 3 or > 4)
        {
            return false;
        }

        if (count == 4)
        {
            if (!core[parts[3]].Equals("0".AsSpan(), StringComparison.Ordinal))
            {
                return false;
            }

            count = 3;
        }

        if (!TryPart(core[parts[0]], out var major)
            || !TryPart(core[parts[1]], out var minor)
            || !TryPart(core[parts[2]], out var patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        if (minor != 0)
        {
            return minor;
        }

        var patch = Patch.CompareTo(other.Patch);
        if (patch != 0)
        {
            return patch;
        }

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public override string ToString()
    {
        var core = Major.ToString(CultureInfo.InvariantCulture)
            + "."
            + Minor.ToString(CultureInfo.InvariantCulture)
            + "."
            + Patch.ToString(CultureInfo.InvariantCulture);
        return PreRelease is null ? core : core + "-" + PreRelease;
    }

    private static bool TryPart(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        if (text.IsEmpty)
        {
            return false;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsValidPreRelease(string preRelease)
    {
        var span = preRelease.AsSpan();
        var start = 0;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i != span.Length && span[i] != '.')
            {
                continue;
            }

            if (i == start)
            {
                return false;
            }

            for (var j = start; j < i; j++)
            {
                var ch = span[j];
                if (ch is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '-')
                {
                    continue;
                }

                return false;
            }

            start = i + 1;
        }

        return true;
    }

    private static int ComparePreRelease(string? left, string? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        // A release build sorts after any pre-release of the same core version.
        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var a = left.Split('.');
        var b = right.Split('.');
        var count = Math.Min(a.Length, b.Length);
        for (var i = 0; i < count; i++)
        {
            var compared = CompareIdentifier(a[i], b[i]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = IsDigits(left);
        var rightNumeric = IsDigits(right);
        if (leftNumeric && rightNumeric)
        {
            if (int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftValue)
                && int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightValue))
            {
                return leftValue.CompareTo(rightValue);
            }

            var byLength = left.Length.CompareTo(right.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
        }

        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static bool IsDigits(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
