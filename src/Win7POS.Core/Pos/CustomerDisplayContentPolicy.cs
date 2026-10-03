using System;
using System.IO;
using System.Linq;
using System.Globalization;

namespace Win7POS.Core.Pos
{
    public sealed class CustomerDisplayLogoReference
    {
        public string FileName { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
    }

    public static class CustomerDisplayContentPolicy
    {
        public const int MaximumLogoBytes = 2 * 1024 * 1024;
        public const long MaximumLogoPixels = 4000000;

        public static bool IsSafeMessage(string value) => value != null && value.Length <= 120 &&
            !value.Any(c => char.IsControl(c) || c == '<' || c == '>') && HasValidUnicode(value);

        private static bool HasValidUnicode(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i]))
                {
                    if (++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
                }
                else if (char.IsLowSurrogate(value[i])) return false;
            }
            return true;
        }

        public static bool IsManagedLogoReference(string file, string hash)
        {
            if (string.IsNullOrEmpty(file)) return string.IsNullOrEmpty(hash);
            return hash != null && hash.Length == 64 && hash.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f') &&
                new[] { ".png", ".jpg", ".bmp" }.Any(ext => file == "logo_" + hash + ext);
        }

        public static string PublicBarcode(string barcode, CustomerDisplayLineKind kind, CustomerDisplayBarcodeMode mode)
        {
            barcode = (barcode ?? string.Empty).Trim();
            if (kind != CustomerDisplayLineKind.Item || !Enum.IsDefined(typeof(CustomerDisplayBarcodeMode), mode) || mode == CustomerDisplayBarcodeMode.Hidden ||
                barcode.StartsWith("DISC:", StringComparison.OrdinalIgnoreCase) ||
                barcode.StartsWith("TAX:", StringComparison.OrdinalIgnoreCase) ||
                barcode.StartsWith("MANUAL:", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (mode != CustomerDisplayBarcodeMode.Last4) return barcode;
            var elements = StringInfo.ParseCombiningCharacters(barcode);
            return elements.Length > 4 ? new string('\u2022', elements.Length - 4) + barcode.Substring(elements[elements.Length - 4]) : barcode;
        }

        // Dimensions are parsed before asking an image decoder to allocate pixels.
        public static string InspectLogoHeader(byte[] bytes, out int width, out int height)
        {
            width = height = 0;
            if (bytes == null || bytes.Length < 24 || bytes.Length > MaximumLogoBytes) throw new InvalidDataException("logo_size");
            string extension;
            if (bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                if (bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R' || Big(bytes, 8) != 13) throw new InvalidDataException("logo_format");
                width = checked((int)Big(bytes, 16)); height = checked((int)Big(bytes, 20)); extension = ".png";
            }
            else if (bytes[0] == 'B' && bytes[1] == 'M' && bytes.Length >= 54)
            {
                if (BitConverter.ToInt32(bytes, 14) < 40) throw new InvalidDataException("logo_format");
                width = BitConverter.ToInt32(bytes, 18); height = Math.Abs(BitConverter.ToInt32(bytes, 22)); extension = ".bmp";
            }
            else if (bytes[0] == 255 && bytes[1] == 216)
            {
                extension = ".jpg";
                var offset = 2;
                while (offset + 4 <= bytes.Length)
                {
                    if (bytes[offset++] != 255) throw new InvalidDataException("logo_format");
                    while (offset < bytes.Length && bytes[offset] == 255) offset++;
                    if (offset >= bytes.Length) break;
                    var marker = bytes[offset++];
                    if (marker == 217 || marker == 218) break;
                    if (marker == 1 || marker >= 208 && marker <= 215) continue;
                    if (offset + 2 > bytes.Length) break;
                    var length = (bytes[offset] << 8) | bytes[offset + 1];
                    if (length < 2 || offset + length > bytes.Length) throw new InvalidDataException("logo_format");
                    if (marker >= 192 && marker <= 195 || marker >= 197 && marker <= 199 || marker >= 201 && marker <= 203 || marker >= 205 && marker <= 207)
                    {
                        if (length < 8) throw new InvalidDataException("logo_format");
                        height = (bytes[offset + 3] << 8) | bytes[offset + 4]; width = (bytes[offset + 5] << 8) | bytes[offset + 6]; break;
                    }
                    offset += length;
                }
            }
            else throw new InvalidDataException("logo_format");
            if (width <= 0 || height <= 0 || (long)width * height > MaximumLogoPixels) throw new InvalidDataException("logo_pixels");
            return extension;
        }

        private static uint Big(byte[] bytes, int offset) =>
            ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
    }
}
