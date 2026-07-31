using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Images;

namespace Win7POS.Data.Images
{
    public sealed class ProductImageStagingOptions
    {
        public ProductImageStagingOptions(
            string rootPath,
            TimeSpan? orphanMinimumAge = null)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("product_image_staging_root_required", nameof(rootPath));
            var root = Path.GetFullPath(rootPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var fileSystemRoot = Path.GetPathRoot(root)?.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (string.Equals(root, fileSystemRoot, StringComparison.OrdinalIgnoreCase) ||
                IsUnderProgramFiles(root))
            {
                throw new ArgumentException("product_image_staging_root_unsafe", nameof(rootPath));
            }
            var age = orphanMinimumAge ?? TimeSpan.FromHours(24);
            if (age < TimeSpan.FromMinutes(5) || age > TimeSpan.FromDays(30))
                throw new ArgumentOutOfRangeException(nameof(orphanMinimumAge));
            RootPath = root;
            OrphanMinimumAge = age;
        }

        public string RootPath { get; }
        public TimeSpan OrphanMinimumAge { get; }

        public static ProductImageStagingOptions CreateDefault()
        {
            return new ProductImageStagingOptions(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Win7POS",
                "ImageStaging"));
        }

        private static bool IsUnderProgramFiles(string path)
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            return roots.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(Path.GetFullPath)
                .Any(root => ProductImageCacheOptions.IsWithin(path, root));
        }
    }

    public sealed class ProductImageStagingPair
    {
        internal ProductImageStagingPair(string mainIdentity, string thumbIdentity)
        {
            MainIdentity = mainIdentity;
            ThumbIdentity = thumbIdentity;
        }

        public string MainIdentity { get; }
        public string ThumbIdentity { get; }
    }

    /// <summary>
    /// Stores only canonical, bounded JPEG variants below a dedicated local-data
    /// root. Durable SQLite rows retain the opaque file names, never source paths.
    /// </summary>
    public sealed class ProductImageStagingStore
    {
        private const string IdentityPrefix = "stage-";
        private readonly ProductImageStagingOptions _options;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public ProductImageStagingStore(ProductImageStagingOptions options = null)
        {
            _options = options ?? ProductImageStagingOptions.CreateDefault();
        }

        public string RootPath => _options.RootPath;

        public async Task<ProductImageStagingPair> StagePairAsync(
            ProductImageProcessedVariant main,
            ProductImageProcessedVariant thumb,
            CancellationToken cancellationToken = default)
        {
            ValidateVariant(main, ProductImageVariant.Main);
            ValidateVariant(thumb, ProductImageVariant.Thumb);
            var nonce = Guid.NewGuid().ToString("N");
            var mainIdentity = IdentityPrefix + nonce + "-main.jpg";
            var thumbIdentity = IdentityPrefix + nonce + "-thumb.jpg";
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureSafeRoot();
                try
                {
                    await WriteAtomicallyAsync(
                        mainIdentity,
                        main.CopyBytes(),
                        cancellationToken).ConfigureAwait(false);
                    await WriteAtomicallyAsync(
                        thumbIdentity,
                        thumb.CopyBytes(),
                        cancellationToken).ConfigureAwait(false);
                    return new ProductImageStagingPair(mainIdentity, thumbIdentity);
                }
                catch
                {
                    DeleteCore(mainIdentity);
                    DeleteCore(thumbIdentity);
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<Stream> OpenVerifiedReadAsync(
            string identity,
            ProductImageVariant variant,
            ProductImageMetadata expected,
            CancellationToken cancellationToken = default)
        {
            RequireIdentity(identity);
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureSafeRoot();
                var path = Resolve(identity);
                byte[] bytes;
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan))
                {
                    if (stream.Length < 1 ||
                        stream.Length > ProductImageContractV1.MaximumBytes(variant))
                    {
                        throw new InvalidDataException("product_image_staged_size_invalid");
                    }
                    bytes = new byte[checked((int)stream.Length)];
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var read = await stream.ReadAsync(
                            bytes,
                            offset,
                            bytes.Length - offset,
                            cancellationToken).ConfigureAwait(false);
                        if (read == 0) throw new EndOfStreamException();
                        offset += read;
                    }
                }
                var validation = ProductImageBinaryPolicy.ValidateCanonicalWireJpeg(
                    bytes,
                    expected);
                if (!validation.IsValid)
                    throw new InvalidDataException("product_image_staged_corrupt");
                return new MemoryStream(bytes, writable: false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task DeletePairAsync(
            string mainIdentity,
            string thumbIdentity,
            CancellationToken cancellationToken = default)
        {
            RequireIdentity(mainIdentity);
            RequireIdentity(thumbIdentity);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureSafeRoot();
                DeleteCore(mainIdentity);
                DeleteCore(thumbIdentity);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<int> CleanupOrphansAsync(
            IEnumerable<string> referencedIdentities,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            var referenced = new HashSet<string>(
                (referencedIdentities ?? Enumerable.Empty<string>())
                    .Where(IsIdentity),
                StringComparer.Ordinal);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureSafeRoot();
                var deleted = 0;
                foreach (var path in Directory.EnumerateFiles(
                    _options.RootPath,
                    IdentityPrefix + "*.jpg",
                    SearchOption.TopDirectoryOnly).Take(4096))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var identity = Path.GetFileName(path);
                    if (referenced.Contains(identity)) continue;
                    DateTime lastWrite;
                    try { lastWrite = File.GetLastWriteTimeUtc(path); }
                    catch { continue; }
                    if (now.UtcDateTime - lastWrite < _options.OrphanMinimumAge) continue;
                    DeleteCore(identity);
                    deleted++;
                }
                foreach (var path in Directory.EnumerateFiles(
                    _options.RootPath,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly).Take(4096))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DateTime lastWrite;
                    try { lastWrite = File.GetLastWriteTimeUtc(path); }
                    catch { continue; }
                    if (now.UtcDateTime - lastWrite < _options.OrphanMinimumAge) continue;
                    File.Delete(path);
                    deleted++;
                }
                return deleted;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static void ValidateVariant(
            ProductImageProcessedVariant value,
            ProductImageVariant variant)
        {
            if (value == null || value.Variant != variant)
                throw new ArgumentException("product_image_staged_variant_invalid");
            var validation = ProductImageBinaryPolicy.ValidateCanonicalWireJpeg(
                value.CopyBytes(),
                value.Metadata);
            if (!validation.IsValid)
                throw new InvalidDataException("product_image_staged_variant_invalid");
        }

        private async Task WriteAtomicallyAsync(
            string identity,
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            var destination = Resolve(identity);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            AssertContained(temporary);
            try
            {
                using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken)
                        .ConfigureAwait(false);
                    stream.Flush(true);
                }
                File.Move(temporary, destination);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }

        private void EnsureSafeRoot()
        {
            AssertExistingParentsHaveNoReparsePoint(_options.RootPath);
            Directory.CreateDirectory(_options.RootPath);
            AssertExistingParentsHaveNoReparsePoint(_options.RootPath);
            var rootInfo = new DirectoryInfo(_options.RootPath);
            if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("product_image_staging_root_reparse");
        }

        private static void AssertExistingParentsHaveNoReparsePoint(string path)
        {
            var current = new DirectoryInfo(Path.GetFullPath(path));
            while (current != null)
            {
                if (current.Exists &&
                    (current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("product_image_staging_path_reparse");
                }
                current = current.Parent;
            }
        }

        private string Resolve(string identity)
        {
            RequireIdentity(identity);
            var path = Path.GetFullPath(Path.Combine(_options.RootPath, identity));
            AssertContained(path);
            return path;
        }

        private void AssertContained(string path)
        {
            if (!ProductImageCacheOptions.IsWithin(path, _options.RootPath) ||
                string.Equals(path, _options.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("product_image_staging_path_escape");
            }
        }

        private void DeleteCore(string identity)
        {
            var path = Resolve(identity);
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("product_image_staging_file_reparse");
            File.Delete(path);
        }

        private static void RequireIdentity(string value)
        {
            if (!IsIdentity(value))
                throw new ArgumentException("product_image_staging_identity_invalid");
        }

        private static bool IsIdentity(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 120 ||
                !value.StartsWith(IdentityPrefix, StringComparison.Ordinal) ||
                !value.EndsWith(".jpg", StringComparison.Ordinal) ||
                value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0)
            {
                return false;
            }
            return value.All(character =>
                (character >= 'a' && character <= 'z') ||
                (character >= '0' && character <= '9') ||
                character == '-' || character == '.');
        }
    }
}
