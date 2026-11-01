namespace Snoop.Inspection.Comparing;

/// <summary>Whether a line is in both texts, only the left, or only the right.</summary>
public enum DiffKind
{
    Same,
    Removed,
    Added,
}

/// <summary>One line of a diff.</summary>
/// <param name="Kind">Where the line is.</param>
/// <param name="LeftNumber">Line number in the left text (from 1), or null when added.</param>
/// <param name="RightNumber">Line number in the right text (from 1), or null when removed.</param>
/// <param name="Text">The line.</param>
public sealed record DiffLine(DiffKind Kind, int? LeftNumber, int? RightNumber, string Text);

/// <summary>
/// A line-by-line diff (longest common subsequence). Common lines at the start and end are matched
/// first, so typical messages that differ in a few places are cheap; the rest is capped at
/// <see cref="MaxMiddleLines"/> lines a side, beyond which the middle is shown as removed then added.
/// </summary>
public static class LineDiff
{
    public const int MaxMiddleLines = 2000;

    public static IReadOnlyList<DiffLine> Compare(string left, string right)
    {
        var a = Split(left);
        var b = Split(right);
        var result = new List<DiffLine>(Math.Max(a.Length, b.Length));

        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            result.Add(new DiffLine(DiffKind.Same, prefix + 1, prefix + 1, a[prefix]));
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }

        var aEnd = a.Length - suffix;
        var bEnd = b.Length - suffix;
        var aCount = aEnd - prefix;
        var bCount = bEnd - prefix;
        if (aCount > MaxMiddleLines || bCount > MaxMiddleLines)
        {
            for (var i = prefix; i < aEnd; i++)
            {
                result.Add(new DiffLine(DiffKind.Removed, i + 1, null, a[i]));
            }

            for (var j = prefix; j < bEnd; j++)
            {
                result.Add(new DiffLine(DiffKind.Added, null, j + 1, b[j]));
            }
        }
        else
        {
            AppendMiddle(result, a, b, prefix, aEnd, bEnd);
        }

        for (var k = 0; k < suffix; k++)
        {
            result.Add(new DiffLine(DiffKind.Same, aEnd + k + 1, bEnd + k + 1, a[aEnd + k]));
        }

        return result;
    }

    /// <summary>Number of lines that are not the same on both sides.</summary>
    public static int CountChanges(IReadOnlyList<DiffLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Count(l => l.Kind != DiffKind.Same);
    }

    private static void AppendMiddle(List<DiffLine> result, string[] a, string[] b, int start, int aEnd, int bEnd)
    {
        var n = aEnd - start;
        var m = bEnd - start;

        // lcs[i, j] = length of the LCS of a[start+i..aEnd) and b[start+j..bEnd).
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[start + i] == b[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[start + x] == b[start + y])
            {
                result.Add(new DiffLine(DiffKind.Same, start + x + 1, start + y + 1, a[start + x]));
                x++;
                y++;
            }
            else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1]))
            {
                result.Add(new DiffLine(DiffKind.Removed, start + x + 1, null, a[start + x]));
                x++;
            }
            else
            {
                result.Add(new DiffLine(DiffKind.Added, null, start + y + 1, b[start + y]));
                y++;
            }
        }
    }

    private static string[] Split(string text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}
