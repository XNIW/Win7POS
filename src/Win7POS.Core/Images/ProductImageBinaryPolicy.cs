using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Win7POS.Core.Images
{
    public sealed class ProductImageHeader
    {
        public ProductImageHeader(
            ProductImageInputFormat format,
            int width,
            int height,
            int orientation)
        {
            Format = format;
            Width = width;
            Height = height;
            Orientation = orientation;
        }

        public ProductImageInputFormat Format { get; }
        public int Width { get; }
        public int Height { get; }
        public int Orientation { get; }
    }

    public static class ProductImageBinaryPolicy
    {
        private static readonly HashSet<byte> SofMarkers = new HashSet<byte>
        {
            0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7,
            0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf
        };

        public static ProductImageValidationResult Inspect(
            byte[] bytes,
            long maximumBytes,
            long maximumPixels,
            out ProductImageHeader header)
        {
            header = null;
            if (bytes == null || bytes.Length < 1 || bytes.Length > maximumBytes)
            {
                return ProductImageValidationResult.Failure(
                    ProductImageValidationCode.InvalidByteSize,
                    "image_input_size_invalid");
            }

            var format = ProductImageInputPolicy.DetectFormat(bytes);
            int width;
            int height;
            int orientation;
            switch (format)
            {
                case ProductImageInputFormat.Jpeg:
                    if (!TryReadJpegHeader(bytes, out width, out height, out orientation))
                    {
                        return ProductImageValidationResult.Failure(
                            ProductImageValidationCode.CorruptImage,
                            "image_jpeg_header_invalid");
                    }
                    break;
                case ProductImageInputFormat.Png:
                    if (!TryReadPngHeader(bytes, out width, out height))
                    {
                        return ProductImageValidationResult.Failure(
                            ProductImageValidationCode.CorruptImage,
                            "image_png_header_invalid");
                    }
                    orientation = 1;
                    break;
                default:
                    return ProductImageValidationResult.Failure(
                        ProductImageValidationCode.UnsupportedMimeType,
                        "image_input_format_unsupported");
            }

            if (width < 1 ||
                height < 1 ||
                width > maximumPixels ||
                height > maximumPixels ||
                (long)width * height > maximumPixels)
            {
                return ProductImageValidationResult.Failure(
                    ProductImageValidationCode.InvalidDimensions,
                    "image_dimensions_invalid");
            }

            header = new ProductImageHeader(format, width, height, orientation);
            return ProductImageValidationResult.Success();
        }

        public static ProductImageValidationResult ValidateCanonicalWireJpeg(
            byte[] bytes,
            ProductImageMetadata expectedMetadata)
        {
            if (expectedMetadata == null)
            {
                throw new ArgumentNullException(nameof(expectedMetadata));
            }

            var inspection = Inspect(
                bytes,
                expectedMetadata.ByteSize,
                ProductImageContractV1.InputMaximumPixels,
                out var header);
            if (!inspection.IsValid)
            {
                return inspection;
            }

            if (header.Format != ProductImageInputFormat.Jpeg ||
                bytes.Length != expectedMetadata.ByteSize ||
                header.Width != expectedMetadata.Width ||
                header.Height != expectedMetadata.Height)
            {
                return ProductImageValidationResult.Failure(
                    ProductImageValidationCode.CorruptImage,
                    "image_wire_metadata_mismatch");
            }

            if (!string.Equals(
                    ProductImageHash.Sha256Hex(bytes),
                    expectedMetadata.Sha256,
                    StringComparison.Ordinal))
            {
                return ProductImageValidationResult.Failure(
                    ProductImageValidationCode.InvalidChecksum,
                    "image_wire_checksum_mismatch");
            }

            if (HasForbiddenJpegMetadata(bytes))
            {
                return ProductImageValidationResult.Failure(
                    ProductImageValidationCode.ForbiddenMetadata,
                    "image_metadata_forbidden");
            }

            return ProductImageValidationResult.Success();
        }

        public static bool HasForbiddenJpegMetadata(byte[] bytes)
        {
            if (bytes == null ||
                bytes.Length < 4 ||
                bytes[0] != 0xff ||
                bytes[1] != 0xd8 ||
                bytes[bytes.Length - 2] != 0xff ||
                bytes[bytes.Length - 1] != 0xd9)
            {
                return true;
            }

            var offset = 2;
            while (offset + 1 < bytes.Length)
            {
                if (bytes[offset] != 0xff)
                {
                    return true;
                }

                while (offset < bytes.Length && bytes[offset] == 0xff)
                {
                    offset++;
                }

                if (offset >= bytes.Length)
                {
                    return true;
                }

                var marker = bytes[offset++];
                if (marker == 0xd9)
                {
                    return offset != bytes.Length;
                }

                if (marker == 0xd8 || marker == 0x01 || (marker >= 0xd0 && marker <= 0xd7))
                {
                    continue;
                }

                if (offset + 1 >= bytes.Length)
                {
                    return true;
                }

                var length = ReadBigEndianUInt16(bytes, offset);
                if (length < 2 || offset + length > bytes.Length)
                {
                    return true;
                }

                var dataStart = offset + 2;
                var dataLength = length - 2;
                var validJfif = marker == 0xe0 &&
                                IsCanonicalJfif(bytes, dataStart, dataLength);
                if (marker == 0xfe ||
                    (marker == 0xe0 && !validJfif) ||
                    (marker >= 0xe1 && marker <= 0xef))
                {
                    return true;
                }

                offset += length;
                if (marker == 0xda)
                {
                    if (!TrySkipEntropy(bytes, ref offset))
                    {
                        return true;
                    }
                }
            }

            return true;
        }

        public static byte[] RemoveForbiddenJpegMetadata(byte[] bytes)
        {
            if (bytes == null ||
                bytes.Length < 4 ||
                bytes[0] != 0xff ||
                bytes[1] != 0xd8)
            {
                throw new InvalidDataException("image_encode_failed");
            }

            using (var output = new MemoryStream(bytes.Length))
            {
                output.WriteByte(0xff);
                output.WriteByte(0xd8);
                var offset = 2;
                while (offset < bytes.Length)
                {
                    var markerStart = offset;
                    if (bytes[offset] != 0xff)
                    {
                        throw new InvalidDataException("image_encode_failed");
                    }

                    while (offset < bytes.Length && bytes[offset] == 0xff)
                    {
                        offset++;
                    }

                    if (offset >= bytes.Length)
                    {
                        throw new InvalidDataException("image_encode_failed");
                    }

                    var marker = bytes[offset++];
                    if (marker == 0xd9)
                    {
                        if (offset != bytes.Length)
                        {
                            throw new InvalidDataException("image_encode_failed");
                        }

                        output.Write(bytes, markerStart, offset - markerStart);
                        break;
                    }

                    if (marker == 0xd8 ||
                        marker == 0x01 ||
                        (marker >= 0xd0 && marker <= 0xd7))
                    {
                        output.Write(bytes, markerStart, offset - markerStart);
                        continue;
                    }

                    if (offset + 1 >= bytes.Length)
                    {
                        throw new InvalidDataException("image_encode_failed");
                    }

                    var length = ReadBigEndianUInt16(bytes, offset);
                    if (length < 2 || offset + length > bytes.Length)
                    {
                        throw new InvalidDataException("image_encode_failed");
                    }

                    var dataStart = offset + 2;
                    var dataLength = length - 2;
                    var validJfif = marker == 0xe0 &&
                                    IsCanonicalJfif(bytes, dataStart, dataLength);
                    var forbidden = marker == 0xfe ||
                                    (marker == 0xe0 && !validJfif) ||
                                    (marker >= 0xe1 && marker <= 0xef);
                    var segmentEnd = offset + length;
                    if (!forbidden)
                    {
                        output.Write(bytes, markerStart, segmentEnd - markerStart);
                    }

                    offset = segmentEnd;
                    if (marker == 0xda)
                    {
                        var entropyStart = offset;
                        if (!TrySkipEntropy(bytes, ref offset))
                        {
                            throw new InvalidDataException("image_encode_failed");
                        }

                        output.Write(bytes, entropyStart, offset - entropyStart);
                    }
                }

                var canonical = output.ToArray();
                if (HasForbiddenJpegMetadata(canonical))
                {
                    throw new InvalidDataException("image_metadata_forbidden");
                }

                return canonical;
            }
        }

        private static bool TryReadPngHeader(
            byte[] bytes,
            out int width,
            out int height)
        {
            width = 0;
            height = 0;
            if (bytes.Length < 24 ||
                bytes[12] != 0x49 ||
                bytes[13] != 0x48 ||
                bytes[14] != 0x44 ||
                bytes[15] != 0x52)
            {
                return false;
            }

            var rawWidth = ReadBigEndianUInt32(bytes, 16);
            var rawHeight = ReadBigEndianUInt32(bytes, 20);
            if (rawWidth == 0 ||
                rawHeight == 0 ||
                rawWidth > int.MaxValue ||
                rawHeight > int.MaxValue)
            {
                return false;
            }

            width = (int)rawWidth;
            height = (int)rawHeight;
            return true;
        }

        private static bool TryReadJpegHeader(
            byte[] bytes,
            out int width,
            out int height,
            out int orientation)
        {
            width = 0;
            height = 0;
            orientation = 1;
            if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8)
            {
                return false;
            }

            var offset = 2;
            var sawSof = false;
            var sawEndOfImage = false;
            while (offset + 1 < bytes.Length)
            {
                if (bytes[offset] != 0xff)
                {
                    return false;
                }

                while (offset < bytes.Length && bytes[offset] == 0xff)
                {
                    offset++;
                }

                if (offset >= bytes.Length)
                {
                    return false;
                }

                var marker = bytes[offset++];
                if (marker == 0xd9)
                {
                    sawEndOfImage = offset == bytes.Length;
                    break;
                }

                if (marker == 0xd8)
                {
                    return false;
                }

                if (marker == 0x01 ||
                    (marker >= 0xd0 && marker <= 0xd7))
                {
                    continue;
                }

                if (offset + 1 >= bytes.Length)
                {
                    return false;
                }

                var length = ReadBigEndianUInt16(bytes, offset);
                if (length < 2 || offset + length > bytes.Length)
                {
                    return false;
                }

                var payloadStart = offset + 2;
                var payloadLength = length - 2;
                if (marker == 0xe1)
                {
                    orientation = TryReadExifOrientation(
                        bytes,
                        payloadStart,
                        payloadLength);
                }

                if (SofMarkers.Contains(marker))
                {
                    if (sawSof)
                    {
                        return false;
                    }

                    if (payloadLength < 6)
                    {
                        return false;
                    }

                    height = ReadBigEndianUInt16(bytes, payloadStart + 1);
                    width = ReadBigEndianUInt16(bytes, payloadStart + 3);
                    if (width < 1 || height < 1)
                    {
                        return false;
                    }

                    sawSof = true;
                }

                offset += length;
                if (marker == 0xda)
                {
                    if (!sawSof || !TrySkipEntropy(bytes, ref offset))
                    {
                        return false;
                    }
                }
            }

            return sawEndOfImage && sawSof && width > 0 && height > 0;
        }

        private static int TryReadExifOrientation(
            byte[] bytes,
            int offset,
            int length)
        {
            try
            {
                if (length < 14 ||
                    bytes[offset] != 0x45 ||
                    bytes[offset + 1] != 0x78 ||
                    bytes[offset + 2] != 0x69 ||
                    bytes[offset + 3] != 0x66 ||
                    bytes[offset + 4] != 0 ||
                    bytes[offset + 5] != 0)
                {
                    return 1;
                }

                var tiff = offset + 6;
                var littleEndian =
                    bytes[tiff] == 0x49 &&
                    bytes[tiff + 1] == 0x49;
                var bigEndian =
                    bytes[tiff] == 0x4d &&
                    bytes[tiff + 1] == 0x4d;
                if (!littleEndian && !bigEndian)
                {
                    return 1;
                }

                var ifdOffset = ReadUInt32(bytes, tiff + 4, littleEndian);
                var ifd = checked(tiff + (int)ifdOffset);
                var payloadEnd = checked(offset + length);
                if (ifd < tiff || ifd + 2 > payloadEnd)
                {
                    return 1;
                }

                var count = ReadUInt16(bytes, ifd, littleEndian);
                var entry = ifd + 2;
                for (var index = 0; index < count; index++, entry += 12)
                {
                    if (entry + 12 > payloadEnd)
                    {
                        return 1;
                    }

                    var tag = ReadUInt16(bytes, entry, littleEndian);
                    if (tag != 0x0112)
                    {
                        continue;
                    }

                    var value = ReadUInt16(bytes, entry + 8, littleEndian);
                    return value >= 1 && value <= 8 ? value : 1;
                }
            }
            catch (Exception)
            {
                return 1;
            }

            return 1;
        }

        private static bool IsCanonicalJfif(
            byte[] bytes,
            int dataStart,
            int dataLength)
        {
            return dataLength == 14 &&
                   dataStart >= 0 &&
                   dataStart + dataLength <= bytes.Length &&
                   bytes[dataStart] == 0x4a &&
                   bytes[dataStart + 1] == 0x46 &&
                   bytes[dataStart + 2] == 0x49 &&
                   bytes[dataStart + 3] == 0x46 &&
                   bytes[dataStart + 4] == 0 &&
                   bytes[dataStart + 5] == 1 &&
                   bytes[dataStart + 7] <= 2 &&
                   bytes[dataStart + 12] == 0 &&
                   bytes[dataStart + 13] == 0;
        }

        private static bool TrySkipEntropy(byte[] bytes, ref int offset)
        {
            while (offset < bytes.Length - 1)
            {
                if (bytes[offset] != 0xff)
                {
                    offset++;
                    continue;
                }

                var markerOffset = offset + 1;
                while (markerOffset < bytes.Length && bytes[markerOffset] == 0xff)
                {
                    markerOffset++;
                }

                if (markerOffset >= bytes.Length)
                {
                    return false;
                }

                var marker = bytes[markerOffset];
                if (marker == 0x00 || (marker >= 0xd0 && marker <= 0xd7))
                {
                    offset = markerOffset + 1;
                    continue;
                }

                return true;
            }

            return false;
        }

        private static ushort ReadBigEndianUInt16(byte[] bytes, int offset)
        {
            return (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
        }

        private static uint ReadBigEndianUInt32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) |
                   ((uint)bytes[offset + 1] << 16) |
                   ((uint)bytes[offset + 2] << 8) |
                   bytes[offset + 3];
        }

        private static ushort ReadUInt16(
            byte[] bytes,
            int offset,
            bool littleEndian)
        {
            return littleEndian
                ? (ushort)(bytes[offset] | (bytes[offset + 1] << 8))
                : ReadBigEndianUInt16(bytes, offset);
        }

        private static uint ReadUInt32(
            byte[] bytes,
            int offset,
            bool littleEndian)
        {
            return littleEndian
                ? (uint)(bytes[offset] |
                         (bytes[offset + 1] << 8) |
                         (bytes[offset + 2] << 16) |
                         (bytes[offset + 3] << 24))
                : ReadBigEndianUInt32(bytes, offset);
        }
    }
}
