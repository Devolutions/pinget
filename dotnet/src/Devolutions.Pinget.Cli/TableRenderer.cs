using System.Text;

namespace Devolutions.Pinget.Cli;

/// <summary>Lays out list/upgrade tables like WinGet's <c>TableOutput</c>.</summary>
internal static class TableRenderer
{
    /// <summary>
    /// Width of the console attached to stdout, as WinGet's <c>GetConsoleWidth</c> reads it (the full
    /// buffer width of STD_OUTPUT). Redirected or piped output has no width, so nothing is truncated.
    /// </summary>
    internal static int? ConsoleWidth()
    {
        if (Console.IsOutputRedirected)
            return null;

        try
        {
            var width = Console.BufferWidth;
            return width > 0 ? width : null;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Renders the table; columns only shrink when <paramref name="consoleWidth"/> is known.</summary>
    internal static List<string> Render(string[] headers, List<string[]> rows, int? consoleWidth)
    {
        var lines = new List<string>();
        if (headers.Length == 0) return lines;
        int cols = headers.Length;
        var widths = headers.Select(DisplayWidth).ToArray();
        var hasData = new bool[cols];

        foreach (var row in rows)
            for (int i = 0; i < Math.Min(cols, row.Length); i++)
                if (!string.IsNullOrEmpty(row[i]))
                {
                    hasData[i] = true;
                    widths[i] = Math.Max(widths[i], DisplayWidth(row[i]));
                }

        for (int i = 0; i < cols; i++)
            if (!hasData[i]) widths[i] = 0;

        var spaceAfter = Enumerable.Repeat(true, cols).ToArray();
        spaceAfter[^1] = false;
        for (int i = cols - 1; i >= 1; i--)
        {
            if (widths[i] == 0) spaceAfter[i - 1] = false;
            else break;
        }

        int totalWidth = widths.Zip(spaceAfter, (w, s) => w + (s ? 1 : 0)).Sum();
        if (consoleWidth is int width && totalWidth >= width)
        {
            int extra = totalWidth - width + 1;
            while (extra > 0)
            {
                int target = 0;
                for (int i = 1; i < cols; i++)
                    if (widths[i] > widths[target]) target = i;
                if (widths[target] > 1) widths[target]--;
                extra--;
            }
            totalWidth = Math.Max(0, width - 1);
        }

        lines.Add(FormatLine(headers, widths, spaceAfter));
        lines.Add(new string('-', totalWidth));
        foreach (var row in rows)
            lines.Add(FormatLine(row, widths, spaceAfter));
        return lines;
    }

    private static string FormatLine(string[] values, int[] widths, bool[] spaceAfter)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(values.Length, widths.Length); i++)
        {
            if (widths[i] == 0) continue;
            var val = values[i] ?? "";
            var valWidth = DisplayWidth(val);
            if (valWidth > widths[i])
            {
                sb.Append(Truncate(val, widths[i]));
                if (spaceAfter[i]) sb.Append(' ');
            }
            else
            {
                sb.Append(val);
                if (spaceAfter[i]) sb.Append(' ', widths[i] - valWidth + 1);
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Console columns <paramref name="value"/> occupies, as WinGet's <c>UTF8ColumnWidth</c> counts them:
    /// East Asian Wide and Fullwidth characters take two columns, combining marks none, everything else one.
    /// </summary>
    internal static int DisplayWidth(string value)
    {
        int width = 0;
        foreach (var rune in value.EnumerateRunes())
            width += RuneWidth(rune);
        return width;
    }

    /// <summary>
    /// Fits <paramref name="value"/> into <paramref name="width"/> columns like WinGet's <c>TableOutput</c>:
    /// the kept prefix is followed by <c>…</c> (a <c>.</c> would read as part of a dotted package id), and a
    /// wide character that no longer fits leaves its column as padding so the result is exactly
    /// <paramref name="width"/> columns.
    /// </summary>
    internal static string Truncate(string value, int width)
    {
        if (DisplayWidth(value) <= width)
            return value;

        int budget = Math.Max(0, width - 1);
        int used = 0;
        var sb = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            int runeWidth = RuneWidth(rune);
            if (used + runeWidth > budget)
                break;
            used += runeWidth;
            sb.Append(rune.ToString());
        }

        sb.Append('…');
        sb.Append(' ', budget - used);
        return sb.ToString();
    }

    // Same ranges as the Rust CLI's char_display_width.
    private static readonly (int Start, int End)[] ZeroWidthRanges =
    [
        (0x0300, 0x036F), (0x1AB0, 0x1AFF), (0x1DC0, 0x1DFF), (0x200D, 0x200D),
        (0x20D0, 0x20FF), (0xFE00, 0xFE0F), (0xFE20, 0xFE2F), (0xE0100, 0xE01EF),
    ];

    // East Asian Width W/F blocks: CJK, kana, Hangul, Yi, fullwidth forms and emoji pictographs.
    private static readonly (int Start, int End)[] WideRanges =
    [
        (0x1100, 0x115F), (0x2E80, 0x303E), (0x3041, 0x33FF), (0x3400, 0x4DBF),
        (0x4E00, 0x9FFF), (0xA000, 0xA4CF), (0xA960, 0xA97F), (0xAC00, 0xD7A3),
        (0xF900, 0xFAFF), (0xFE10, 0xFE19), (0xFE30, 0xFE6F), (0xFF00, 0xFF60),
        (0xFFE0, 0xFFE6), (0x1B000, 0x1B2FF), (0x1F300, 0x1F64F), (0x1F900, 0x1F9FF),
        (0x20000, 0x2FFFD), (0x30000, 0x3FFFD),
    ];

    private static int RuneWidth(Rune rune)
    {
        int code = rune.Value;
        if (ZeroWidthRanges.Any(range => code >= range.Start && code <= range.End))
            return 0;
        return WideRanges.Any(range => code >= range.Start && code <= range.End) ? 2 : 1;
    }
}
