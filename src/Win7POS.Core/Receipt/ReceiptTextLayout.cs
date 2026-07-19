using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Win7POS.Core.Receipt
{
    /// <summary>
    /// Shared text-layout primitives for 32/42-column thermal output.
    /// Width is measured in terminal cells so East-Asian wide characters do not
    /// silently overflow the configured paper profile.
    /// </summary>
    public static class ReceiptTextLayout
    {
        public static int NormalizeColumns(int columns)
            => Math.Max(16, Math.Min(96, columns));

        public static string SafeText(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        }

        public static int VisibleWidth(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            var width = 0;
            for (var index = 0; index < value.Length; index++)
            {
                var codePoint = char.ConvertToUtf32(value, index);
                if (char.IsHighSurrogate(value[index])) index++;
                var category = CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(codePoint), 0);
                if (category == UnicodeCategory.NonSpacingMark ||
                    category == UnicodeCategory.EnclosingMark ||
                    category == UnicodeCategory.Format)
                {
                    continue;
                }
                width += IsWide(codePoint) ? 2 : 1;
            }
            return width;
        }

        public static IReadOnlyList<string> WrapText(string value, int columns)
        {
            var width = NormalizeColumns(columns);
            var input = SafeText(value);
            if (input.Length == 0) return new[] { string.Empty };

            var lines = new List<string>();
            var current = new StringBuilder();
            var currentWidth = 0;
            var lastBreakIndex = -1;

            for (var index = 0; index < input.Length; index++)
            {
                var codePoint = char.ConvertToUtf32(input, index);
                var text = char.ConvertFromUtf32(codePoint);
                if (char.IsHighSurrogate(input[index])) index++;
                var cellWidth = VisibleWidth(text);

                if (currentWidth + cellWidth > width && current.Length > 0)
                {
                    if (lastBreakIndex >= 0)
                    {
                        var raw = current.ToString();
                        lines.Add(raw.Substring(0, lastBreakIndex).TrimEnd());
                        var remainder = raw.Substring(lastBreakIndex + 1).TrimStart();
                        current.Clear();
                        current.Append(remainder);
                        currentWidth = VisibleWidth(remainder);
                    }
                    else
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                        currentWidth = 0;
                    }
                    lastBreakIndex = LastWhitespaceIndex(current);
                }

                current.Append(text);
                currentWidth += cellWidth;
                if (char.IsWhiteSpace(text, 0)) lastBreakIndex = current.Length - text.Length;
            }

            if (current.Length > 0) lines.Add(current.ToString().TrimEnd());
            return lines;
        }

        public static string PadLeft(string value, int columns)
        {
            var safe = SafeText(value);
            return new string(' ', Math.Max(0, columns - VisibleWidth(safe))) + safe;
        }

        public static string PadRight(string value, int columns)
        {
            var safe = SafeText(value);
            return safe + new string(' ', Math.Max(0, columns - VisibleWidth(safe)));
        }

        public static IReadOnlyList<string> TwoColumnLine(string left, string right, int columns)
        {
            var width = NormalizeColumns(columns);
            var safeRight = SafeText(right);
            var rightWidth = VisibleWidth(safeRight);
            if (rightWidth >= width)
            {
                var overflow = new List<string>(WrapText(left, width));
                foreach (var part in WrapText(safeRight, width)) overflow.Add(PadLeft(part, width));
                return overflow;
            }

            var leftWidth = Math.Max(1, width - rightWidth - 1);
            var leftParts = new List<string>(WrapText(left, leftWidth));
            if (leftParts.Count == 0) leftParts.Add(string.Empty);
            var result = new List<string>();
            for (var index = 0; index < leftParts.Count - 1; index++) result.Add(leftParts[index]);
            var last = leftParts[leftParts.Count - 1];
            result.Add(PadRight(last, width - rightWidth) + safeRight);
            return result;
        }

        public static string Separator(int columns, char character = '-')
            => new string(character, NormalizeColumns(columns));

        public static string Center(string value, int columns)
        {
            var width = NormalizeColumns(columns);
            var safe = SafeText(value);
            var visible = VisibleWidth(safe);
            return new string(' ', Math.Max(0, (width - visible) / 2)) + safe;
        }

        private static int LastWhitespaceIndex(StringBuilder value)
        {
            for (var index = value.Length - 1; index >= 0; index--)
                if (char.IsWhiteSpace(value[index])) return index;
            return -1;
        }

        private static bool IsWide(int codePoint)
        {
            return codePoint >= 0x1100 &&
                   (codePoint <= 0x115F ||
                    codePoint == 0x2329 || codePoint == 0x232A ||
                    (codePoint >= 0x2E80 && codePoint <= 0xA4CF && codePoint != 0x303F) ||
                    (codePoint >= 0xAC00 && codePoint <= 0xD7A3) ||
                    (codePoint >= 0xF900 && codePoint <= 0xFAFF) ||
                    (codePoint >= 0xFE10 && codePoint <= 0xFE19) ||
                    (codePoint >= 0xFE30 && codePoint <= 0xFE6F) ||
                    (codePoint >= 0xFF00 && codePoint <= 0xFF60) ||
                    (codePoint >= 0xFFE0 && codePoint <= 0xFFE6) ||
                    (codePoint >= 0x1F300 && codePoint <= 0x1FAFF) ||
                    (codePoint >= 0x20000 && codePoint <= 0x3FFFD));
        }
    }
}
