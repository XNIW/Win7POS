using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Win7POS.Core.Backup;

namespace Win7POS.Data.Backup
{
    public sealed class BackupAutomationOptions
    {
        public BackupScheduleOptions Schedule { get; set; } = new BackupScheduleOptions();
        public int RetentionMaxCount { get; set; } = 14;
        public int RetentionMaxAgeDays { get; set; } = 30;
        public string DestinationKind { get; set; } = "local";
        public string DestinationPath { get; set; } = string.Empty;

        internal BackupAutomationOptions Copy()
        {
            return new BackupAutomationOptions
            {
                Schedule = new BackupScheduleOptions
                {
                    Mode = Schedule.Mode,
                    LocalTime = Schedule.LocalTime,
                    WeeklyDay = Schedule.WeeklyDay,
                    CatchUpOnStartup = Schedule.CatchUpOnStartup
                },
                RetentionMaxCount = RetentionMaxCount,
                RetentionMaxAgeDays = RetentionMaxAgeDays,
                DestinationKind = DestinationKind,
                DestinationPath = DestinationPath
            };
        }
    }

    public sealed class BackupAutomationResult
    {
        public string Path { get; internal set; } = string.Empty;
        public string Code { get; internal set; } = string.Empty;
        public bool IsSuccess { get; internal set; }
        public int DeletedCount { get; internal set; }
    }

    internal interface IBackupAutomationClock
    {
        DateTime LocalNow { get; }
        DateTime UtcNow { get; }
        TimeSpan MonotonicElapsed { get; }
    }

    internal sealed class BackupAutomationClock : IBackupAutomationClock
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        public DateTime LocalNow => DateTime.Now;
        public DateTime UtcNow => DateTime.UtcNow;
        public TimeSpan MonotonicElapsed => _elapsed.Elapsed;
    }

    internal static class BackupAutomationDestination
    {
        internal static string Resolve(BackupAutomationOptions options, string defaultDirectory, string livePath)
        {
            if (options == null || options.Schedule == null)
                throw new ArgumentException("Backup options are required.");
            BackupSchedulePolicy.Validate(options.Schedule);
            if (options.RetentionMaxCount < 3 || options.RetentionMaxCount > 365)
                throw new ArgumentException("Backup retention count must be between 3 and 365.");
            if (options.RetentionMaxAgeDays < 1 || options.RetentionMaxAgeDays > 3650)
                throw new ArgumentException("Backup retention days must be between 1 and 3650.");
            if (options.DestinationKind != "local" && options.DestinationKind != "custom_local" &&
                options.DestinationKind != "network_share")
                throw new ArgumentException("Backup destination kind is invalid.");

            var value = options.DestinationKind == "local" ? defaultDirectory : options.DestinationPath;
            value = value ?? string.Empty;
            if (value.Length == 0 || value != value.Trim() || value.Any(char.IsControl) ||
                value.IndexOfAny(new[] { '@', '?', '*', '"', '<', '>', '|', '\0' }) >= 0 ||
                value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                value.Split(new[] { '\\', '/' }).Any(part => part == "." || part == ".." ||
                    part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.IndexOf('~') >= 0))
                throw new ArgumentException("Backup destination must be an absolute directory without credentials or traversal.");

            var unc = value.StartsWith(@"\\", StringComparison.Ordinal);
            if (options.DestinationKind == "network_share")
            {
                var parts = value.Substring(unc ? 2 : 0).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (!unc || parts.Length < 2 || parts.Any(part => part.IndexOf(':') >= 0) ||
                    parts[0] == "." || parts[0] == "?" || parts[1].Length == 0)
                    throw new ArgumentException("Network backup destination requires a UNC server and share.");
            }
            else if (unc || value.Length < 3 || !char.IsLetter(value[0]) || value[1] != ':' ||
                (value[2] != '\\' && value[2] != '/') || value.Substring(2).IndexOf(':') >= 0)
            {
                throw new ArgumentException("Local backup destination requires an absolute drive path.");
            }

            var full = System.IO.Path.GetFullPath(value).TrimEnd('\\', '/');
            if (full.Length <= 2 || string.Equals(full, System.IO.Path.GetFullPath(livePath), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Backup destination must differ from the live database.");
            // Reserve room for the immutable final name and the engine's temporary suffix on Windows 7.
            if (full.Length + 104 >= 240)
                throw new ArgumentException("Backup destination is too long for a Windows 7 snapshot.");
            foreach (var protectedDirectory in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432")
            })
            {
                if (!string.IsNullOrWhiteSpace(protectedDirectory))
                {
                    var protectedPath = System.IO.Path.GetFullPath(protectedDirectory).TrimEnd('\\', '/');
                    if (IsWithin(full, protectedPath) || (unc && full.Split('\\').Any(part =>
                        string.Equals(part, System.IO.Path.GetFileName(protectedPath), StringComparison.OrdinalIgnoreCase))))
                        throw new ArgumentException("Backups cannot be stored in Program Files.");
                }
            }
            return full;
        }

        internal static bool IsWithin(string path, string directory)
        {
            var prefix = directory.TrimEnd('\\', '/');
            return string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(prefix + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal static void RejectReparseAncestors(string directory)
        {
            for (var current = directory; !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
            {
                try
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Backup destination contains a reparse point.");
                    if ((attributes & FileAttributes.Directory) == 0)
                        throw new IOException("Backup destination contains a file component.");
                }
                catch (DirectoryNotFoundException) { }
                catch (FileNotFoundException) { }
            }
        }

        internal static string SafeActor(string actor)
        {
            // Actor is an identifier only; never persist supplied paths, credentials, or display text.
            var value = (actor ?? "unknown").Trim();
            if (value.Length == 0 || value.Length > 48 || value.Any(character =>
                !char.IsLetterOrDigit(character) && character != '_' && character != '-' && character != '.'))
                return "operator";
            return value;
        }

    }
}
