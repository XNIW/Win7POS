using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Win7POS.Core.Logging
{
    /// <summary>
    /// File-system boundary invoked only by the bounded background writer.
    /// </summary>
    public sealed class RotatingFileLogSink : ILogBatchSink
    {
        public const long DefaultMaxLogBytes = 5L * 1024L * 1024L;
        public const int DefaultRetainedLogFiles = 5;

        private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
        private readonly object _gate = new object();
        private readonly string _logPath;
        private readonly long _maxLogBytes;
        private readonly int _retainedLogFiles;

        public RotatingFileLogSink(
            string logPath,
            long maxLogBytes = DefaultMaxLogBytes,
            int retainedLogFiles = DefaultRetainedLogFiles)
        {
            if (string.IsNullOrWhiteSpace(logPath))
                throw new ArgumentException("A log path is required.", nameof(logPath));
            if (maxLogBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxLogBytes));
            if (retainedLogFiles <= 0)
                throw new ArgumentOutOfRangeException(nameof(retainedLogFiles));

            _logPath = logPath;
            _maxLogBytes = maxLogBytes;
            _retainedLogFiles = retainedLogFiles;
        }

        public void WriteBatch(IReadOnlyList<string> lines)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            if (lines.Count == 0) return;

            var batch = new StringBuilder();
            for (var index = 0; index < lines.Count; index++)
            {
                if (!string.IsNullOrEmpty(lines[index]))
                {
                    batch.Append(lines[index]);
                }
            }

            if (batch.Length == 0) return;

            var bytes = Utf8WithoutBom.GetBytes(batch.ToString());
            lock (_gate)
            {
                var parent = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                RotateIfNeeded(bytes.LongLength);
                using (var stream = new FileStream(
                    _logPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    useAsync: false))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
            }
        }

        private void RotateIfNeeded(long incomingBytes)
        {
            if (!File.Exists(_logPath))
            {
                return;
            }

            var currentBytes = new FileInfo(_logPath).Length;
            if (currentBytes == 0 || currentBytes + incomingBytes <= _maxLogBytes)
            {
                return;
            }

            for (var index = _retainedLogFiles - 1; index >= 1; index--)
            {
                var source = RotatedPath(index);
                var target = RotatedPath(index + 1);
                if (!File.Exists(source))
                {
                    continue;
                }

                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(source, target);
            }

            var firstArchive = RotatedPath(1);
            if (File.Exists(firstArchive))
            {
                File.Delete(firstArchive);
            }

            File.Move(_logPath, firstArchive);
        }

        private string RotatedPath(int index)
        {
            return _logPath + "." + index.ToString(CultureInfo.InvariantCulture);
        }
    }
}
