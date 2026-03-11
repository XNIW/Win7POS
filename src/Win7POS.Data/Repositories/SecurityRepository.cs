using System;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Util;

namespace Win7POS.Data.Repositories
{
    public sealed class SecurityRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public SecurityRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task LogEventAsync(string eventType, string details, int? userId = null)
        {
            var ts = UnixTime.NowSeconds();
            using var conn = _factory.Open();
            await conn.ExecuteAsync(
                "INSERT INTO security_events(ts, user_id, event_type, details) VALUES(@ts, @userId, @eventType, @details)",
                new { ts, userId, eventType = eventType ?? "", details = details ?? "" }).ConfigureAwait(false);
        }
    }
}
