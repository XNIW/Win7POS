using System;
using System.Collections.Generic;
using System.IO;

internal static class ConsoleFormat
{
    public static void PrintSection(TextWriter writer, string title)
    {
        writer.WriteLine(title ?? string.Empty);
    }

    public static void PrintKeyValues(TextWriter writer, IReadOnlyList<KeyValuePair<string, string>> items)
    {
        var width = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var len = (items[i].Key ?? string.Empty).Length;
            if (len > width) width = len;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var k = items[i].Key ?? string.Empty;
            var v = items[i].Value ?? string.Empty;
            writer.WriteLine($"{k.PadRight(width)} : {v}");
        }
    }

    public static void PrintTable(
        TextWriter writer,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        int maxWidthPerCol,
        int maxRows)
    {
        if (headers == null || headers.Count == 0) return;
        if (maxWidthPerCol < 6) maxWidthPerCol = 6;
        if (maxRows < 0) maxRows = 0;

        var widths = new int[headers.Count];
        for (var i = 0; i < headers.Count; i++)
        {
            widths[i] = Trunc(headers[i], maxWidthPerCol).Length;
        }

        var take = rows.Count < maxRows ? rows.Count : maxRows;
        for (var r = 0; r < take; r++)
        {
            var row = rows[r];
            for (var c = 0; c < headers.Count; c++)
            {
                var val = c < row.Count ? row[c] : string.Empty;
                var len = Trunc(val, maxWidthPerCol).Length;
                if (len > widths[c]) widths[c] = len;
            }
        }

        WriteRow(writer, headers, widths, maxWidthPerCol);
        var sep = "";
        for (var i = 0; i < headers.Count; i++)
        {
            if (i > 0) sep += "-+-";
            sep += new string('-', widths[i]);
        }
        writer.WriteLine(sep);

        for (var r = 0; r < take; r++)
        {
            WriteRow(writer, rows[r], widths, maxWidthPerCol);
        }
    }

    private static void WriteRow(TextWriter writer, IReadOnlyList<string> row, int[] widths, int maxWidth)
    {
        for (var i = 0; i < widths.Length; i++)
        {
            if (i > 0) writer.Write(" | ");
            var val = i < row.Count ? row[i] : string.Empty;
            writer.Write(Trunc(val, maxWidth).PadRight(widths[i]));
        }
        writer.WriteLine();
    }

    private static string Trunc(string value, int width)
    {
        var text = value ?? string.Empty;
        if (text.Length <= width) return text;
        if (width <= 3) return text.Substring(0, width);
        return text.Substring(0, width - 3) + "...";
    }
}
