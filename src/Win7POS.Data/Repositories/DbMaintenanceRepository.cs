using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Win7POS.Data.Repositories
{
    public sealed class DbMaintenanceRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public DbMaintenanceRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<string> IntegrityCheckAsync()
        {
            using var conn = _factory.Open();
            return await ReadIntegrityCheckAsync(conn, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<string> ForeignKeyCheckAsync()
        {
            using var conn = _factory.Open();
            return await ReadForeignKeyCheckAsync(conn, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<DatabaseValidationResult> ValidateAsync()
        {
            return await ValidateAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<DatabaseValidationResult> ValidateAsync(CancellationToken cancellationToken)
        {
            return await ValidateAsync(null, cancellationToken).ConfigureAwait(false);
        }

        internal async Task<DatabaseValidationResult> ValidateAsync(
            Action<DatabaseValidationPhase> phaseCompleted,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var conn = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var integrity = await ReadIntegrityCheckAsync(conn, cancellationToken).ConfigureAwait(false);
            phaseCompleted?.Invoke(DatabaseValidationPhase.Integrity);
            cancellationToken.ThrowIfCancellationRequested();
            var foreignKeys = await ReadForeignKeyCheckAsync(conn, cancellationToken).ConfigureAwait(false);
            phaseCompleted?.Invoke(DatabaseValidationPhase.ForeignKeys);
            return new DatabaseValidationResult
            {
                ForeignKeyCheck = foreignKeys,
                IntegrityCheck = integrity
            };
        }

        public async Task VacuumAsync()
        {
            using var conn = _factory.Open();
            await conn.ExecuteAsync("VACUUM;").ConfigureAwait(false);
        }

        public async Task<WalCheckpointResult> WalCheckpointAsync()
        {
            using var conn = _factory.Open();
            var result = await conn.QuerySingleAsync<WalCheckpointResult>("PRAGMA wal_checkpoint(FULL);").ConfigureAwait(false);
            if (result.Busy != 0)
            {
                throw new InvalidOperationException("SQLite WAL checkpoint incomplete: busy=" + result.Busy);
            }

            return result;
        }

        private static async Task<string> ReadIntegrityCheckAsync(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            CancellationToken cancellationToken)
        {
            var list = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        list.Add(Convert.ToString(reader.GetValue(0)) ?? string.Empty);
                }
            }
            if (list.Count == 0) return "OK";
            return string.Join(Environment.NewLine, list);
        }

        private static async Task<string> ReadForeignKeyCheckAsync(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            CancellationToken cancellationToken)
        {
            var violations = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_key_check;";
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        violations.Add(
                            Convert.ToString(reader.GetValue(0)) + "|" +
                            Convert.ToString(reader.GetValue(1)) + "|" +
                            Convert.ToString(reader.GetValue(2)) + "|" +
                            Convert.ToString(reader.GetValue(3)));
                    }
                }
            }

            return violations.Count == 0
                ? "OK"
                : string.Join(Environment.NewLine, violations);
        }
    }

    internal enum DatabaseValidationPhase
    {
        Integrity,
        ForeignKeys
    }

    public sealed class DatabaseValidationResult
    {
        public string ForeignKeyCheck { get; set; } = string.Empty;
        public string IntegrityCheck { get; set; } = string.Empty;

        public bool IsValid =>
            string.Equals((IntegrityCheck ?? string.Empty).Trim(), "ok", StringComparison.OrdinalIgnoreCase) &&
            string.Equals((ForeignKeyCheck ?? string.Empty).Trim(), "ok", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class WalCheckpointResult
    {
        public int Busy { get; set; }
        public int Checkpointed { get; set; }
        public int Log { get; set; }
    }
}
