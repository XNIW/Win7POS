using System;

namespace Win7POS.Core.Backup
{

    public enum BackupScheduleMode
    {
        Disabled,
        Daily,
        Weekly
    }

    public sealed class BackupScheduleOptions
    {
        public BackupScheduleMode Mode { get; set; } = BackupScheduleMode.Disabled;
        public string LocalTime { get; set; } = "02:00";
        public DayOfWeek WeeklyDay { get; set; } = DayOfWeek.Sunday;
        public bool CatchUpOnStartup { get; set; } = true;
    }

    /// <summary>
    /// Identifies schedule slots by local wall-clock time, independent of UTC offset.
    /// The caller persists a monotonically increasing considered-through slot after
    /// success or an intentional startup skip; failures leave that watermark intact.
    /// </summary>
    public static class BackupSchedulePolicy
    {
        public static void Validate(BackupScheduleOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (options.Mode != BackupScheduleMode.Disabled &&
                options.Mode != BackupScheduleMode.Daily &&
                options.Mode != BackupScheduleMode.Weekly)
                throw new ArgumentOutOfRangeException(nameof(options.Mode));
            if (!TryParseLocalTime(options.LocalTime, out _))
                throw new ArgumentException("Backup time must use invariant HH:mm (00:00–23:59).", nameof(options.LocalTime));
            if ((int)options.WeeklyDay < (int)DayOfWeek.Sunday ||
                (int)options.WeeklyDay > (int)DayOfWeek.Saturday)
                throw new ArgumentOutOfRangeException(nameof(options.WeeklyDay));
        }

        public static bool TryParseLocalTime(string value, out TimeSpan localTime)
        {
            localTime = TimeSpan.Zero;
            if (value == null || value.Length != 5 || value[2] != ':' ||
                value[0] < '0' || value[0] > '9' ||
                value[1] < '0' || value[1] > '9' ||
                value[3] < '0' || value[3] > '9' ||
                value[4] < '0' || value[4] > '9')
                return false;

            var hours = (value[0] - '0') * 10 + value[1] - '0';
            var minutes = (value[3] - '0') * 10 + value[4] - '0';
            if (hours > 23 || minutes > 59)
                return false;

            localTime = new TimeSpan(hours, minutes, 0);
            return true;
        }

        /// <summary>Returns only the most recent slot at or before localNow.</summary>
        public static DateTime? GetLatestSlot(BackupScheduleOptions options, DateTime localNow)
        {
            Validate(options);
            if (options.Mode == BackupScheduleMode.Disabled)
                return null;

            TryParseLocalTime(options.LocalTime, out var time);
            var now = AsWallClock(localNow);
            var daysBack = options.Mode == BackupScheduleMode.Weekly
                ? ((int)now.DayOfWeek - (int)options.WeeklyDay + 7) % 7
                : 0;
            var todayAtTime = now.Date.Add(time);
            if (daysBack == 0 && todayAtTime > now)
                daysBack = options.Mode == BackupScheduleMode.Weekly ? 7 : 1;
            if (todayAtTime.Ticks < daysBack * TimeSpan.TicksPerDay)
                return null;
            return todayAtTime.AddDays(-daysBack);
        }

        /// <summary>
        /// Returns one due slot after the durable watermark and no earlier than the
        /// policy activation. A startup skip must also advance the caller's watermark,
        /// otherwise a later ordinary tick could reinterpret it as due.
        /// </summary>
        public static DateTime? GetDueSlot(
            BackupScheduleOptions options,
            DateTime localNow,
            DateTime? consideredThrough,
            DateTime? activatedAt,
            bool startup)
        {
            var latest = GetLatestSlot(options, localNow);
            if (!latest.HasValue)
                return null;

            var slot = latest.Value;
            if (consideredThrough.HasValue && slot <= AsWallClock(consideredThrough.Value))
                return null;
            if (activatedAt.HasValue && slot < AsWallClock(activatedAt.Value))
                return null;
            if (startup && !options.CatchUpOnStartup && slot < AsWallClock(localNow))
                return null;
            return slot;
        }

        private static DateTime AsWallClock(DateTime value) =>
            DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }
}
