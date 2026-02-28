using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Win7POS.Data.Repositories
{
    public sealed class AuditLogRepository
    {
        public async Task AppendAsync(PosDbOptions options, long tsMs, string action, string details)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            using var conn = new SqliteConnectionFactory(options).Open();
            await conn.ExecuteAsync(
                "INSERT INTO audit_log(ts, action, details) VALUES (@ts, @action, @details)",
                new { ts = tsMs, action = action ?? string.Empty, details = details ?? string.Empty });
        }

        public async Task AppendAsync(DbConnection conn, DbTransaction tx, long tsMs, string action, string details)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            await conn.ExecuteAsync(
                "INSERT INTO audit_log(ts, action, details) VALUES (@ts, @action, @details)",
                new { ts = tsMs, action = action ?? string.Empty, details = details ?? string.Empty },
                tx);
        }

        public async Task<List<AuditLogRow>> GetRecentAsync(PosDbOptions options, int limit)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (limit <= 0) limit = 100;
            using var conn = new SqliteConnectionFactory(options).Open();
            var rows = await conn.QueryAsync<AuditLogRow>(
                @"SELECT ts AS Ts, action AS Action, details AS Details
                  FROM audit_log
                  ORDER BY ts DESC
                  LIMIT @limit",
                new { limit });
            return rows.ToList();
        }
    }

    public sealed class AuditLogRow
    {
        public long Ts { get; set; }
        public string Action { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }
}
