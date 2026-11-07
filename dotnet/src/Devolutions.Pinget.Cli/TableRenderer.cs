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
        var widths = headers.Select(h => h.Length).ToArray();
        var hasData = new bool[cols];

        foreach (var row in rows)
            for (int i = 0; i < Math.Min(cols, row.Length); i++)
                if (!string.IsNullOrEmpty(row[i]))
                {
                    hasData[i] = true;
                    widths[i] = Math.Max(widths[i], row[i].Length);
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
            if (val.Length > widths[i])
            {
                sb.Append(Truncate(val, widths[i]));
                if (spaceAfter[i]) sb.Append(' ');
            }
            else
            {
                sb.Append(val);
                if (spaceAfter[i]) sb.Append(' ', widths[i] - val.Length + 1);
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + ".";
}
