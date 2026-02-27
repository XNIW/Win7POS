using System;
using System.IO;
using System.Text;
using Win7POS.Core;

namespace Win7POS.Wpf.Infrastructure
{
    public sealed class FileLogger
    {
        public void LogInfo(string message)
        {
            WriteLine("INFO", message ?? string.Empty);
        }

        public void LogError(Exception ex, string context)
        {
            var msg = (context ?? string.Empty) + " | " + (ex == null ? string.Empty : ex.ToString());
            WriteLine("ERROR", msg);
        }

        private static void WriteLine(string level, string message)
        {
            try
            {
                AppPaths.EnsureDataDirectories();
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                           " [" + level + "] " + message + Environment.NewLine;
                File.AppendAllText(AppPaths.LogPath, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never break POS flow.
            }
        }
    }
}
