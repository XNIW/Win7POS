using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Win7POS.Core.Pos;

namespace Win7POS.Wpf.Pos.CustomerDisplay
{
    public sealed class CustomerDisplayLogoStore
    {
        private readonly string _directory;
        private readonly Dictionary<string, BitmapSource> _cache = new Dictionary<string, BitmapSource>(StringComparer.Ordinal);

        public CustomerDisplayLogoStore(string directory)
        {
            _directory = LocalPath(directory);
        }

        public Task<CustomerDisplayLogoReference> ImportAsync(string sourcePath) => Task.Run(() =>
        {
            var bytes = ReadBounded(LocalPath(sourcePath));
            var extension = CustomerDisplayContentPolicy.InspectLogoHeader(bytes, out var width, out var height);
            var bitmap = Decode(bytes, width, height);
            var hash = Hash(bytes);
            var reference = new CustomerDisplayLogoReference { FileName = "logo_" + hash + extension, Hash = hash };
            RejectReparseAncestors(_directory);
            Directory.CreateDirectory(_directory);
            var final = Path.Combine(_directory, reference.FileName);
            if (File.Exists(final))
            {
                if (Hash(ReadBounded(final)) != hash) throw new InvalidDataException("logo_changed");
            }
            else
            {
                var partial = final + ".partial-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    // No unverified final file or original source path is published.
                    File.Move(partial, final);
                }
                finally { if (File.Exists(partial)) File.Delete(partial); }
            }
            lock (_cache) { _cache.Clear(); _cache[reference.FileName] = bitmap; }
            return reference;
        });

        public Task<BitmapSource> LoadSafeAsync(CustomerDisplaySettings settings) => Task.Run(() =>
        {
            if (string.IsNullOrEmpty(settings.LogoFile)) return null;
            try
            {
                if (!CustomerDisplayContentPolicy.IsManagedLogoReference(settings.LogoFile, settings.LogoHash)) return null;
                var bytes = ReadBounded(Path.Combine(_directory, settings.LogoFile));
                if (Hash(bytes) != settings.LogoHash) return null;
                CustomerDisplayContentPolicy.InspectLogoHeader(bytes, out var width, out var height);
                var bitmap = Decode(bytes, width, height);
                lock (_cache) { _cache.Clear(); _cache[settings.LogoFile] = bitmap; }
                return bitmap;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is FormatException || error is NotSupportedException || error is System.Runtime.InteropServices.COMException || error is OverflowException)
            {
                lock (_cache) _cache.Remove(settings.LogoFile);
                return null;
            }
        });

        public BitmapSource Cached(CustomerDisplaySettings settings)
        {
            lock (_cache) return _cache.TryGetValue(settings.LogoFile ?? string.Empty, out var value) ? value : null;
        }

        private static BitmapSource Decode(byte[] bytes, int width, int height)
        {
            using (var input = new MemoryStream(bytes, false))
            {
                var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
                if (!(decoder is PngBitmapDecoder) && !(decoder is JpegBitmapDecoder) && !(decoder is BmpBitmapDecoder))
                    throw new InvalidDataException("logo_format");
                if (decoder.Frames.Count != 1) throw new InvalidDataException("logo_frames");
                var frame = decoder.Frames[0];
                if (frame.PixelWidth != width || frame.PixelHeight != height || (long)frame.PixelWidth * frame.PixelHeight > CustomerDisplayContentPolicy.MaximumLogoPixels)
                    throw new InvalidDataException("logo_pixels");
                var bitmap = new WriteableBitmap(frame);
                bitmap.Freeze();
                return bitmap;
            }
        }

        private static byte[] ReadBounded(string path)
        {
            RejectReparseAncestors(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length < 24 || stream.Length > CustomerDisplayContentPolicy.MaximumLogoBytes) throw new InvalidDataException("logo_size");
                var bytes = new byte[(int)stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var count = stream.Read(bytes, read, bytes.Length - read);
                    if (count == 0) throw new EndOfStreamException("logo_changed");
                    read += count;
                }
                if (stream.ReadByte() != -1) throw new InvalidDataException("logo_changed");
                return bytes;
            }
        }

        private static string LocalPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 3 || !char.IsLetter(value[0]) || value[1] != ':' ||
                value[2] != '\\' && value[2] != '/' || !Path.IsPathRooted(value) || value.StartsWith("\\\\", StringComparison.Ordinal) ||
                value.IndexOf("://", StringComparison.Ordinal) >= 0 || value.Substring(2).Contains(":"))
                throw new ArgumentException("logo_local_path");
            return Path.GetFullPath(value);
        }

        private static void RejectReparseAncestors(string path)
        {
            for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("logo_reparse_path");
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
