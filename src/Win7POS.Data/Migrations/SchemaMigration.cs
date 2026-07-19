using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data.Migrations
{
    public sealed class SchemaMigration
    {
        private static readonly Regex MigrationIdPattern = new Regex(
            "^[0-9]{4}-[a-z0-9]+(?:-[a-z0-9]+)*$",
            RegexOptions.CultureInvariant);

        private readonly Action<SqliteConnection, SqliteTransaction> _apply;
        private readonly Func<LegacySchemaDetector, bool> _isSatisfied;
        private readonly Func<LegacySchemaDetector, bool> _recognizesLedgerlessBaseline;

        public SchemaMigration(
            string migrationId,
            string description,
            string checksumMaterial,
            string minimumApplicationVersion,
            string rollbackCompatibility,
            bool requiresBackup,
            Action<SqliteConnection, SqliteTransaction> apply,
            Func<LegacySchemaDetector, bool> isSatisfied)
            : this(
                migrationId,
                description,
                checksumMaterial,
                null,
                minimumApplicationVersion,
                rollbackCompatibility,
                requiresBackup,
                apply,
                isSatisfied,
                null)
        {
        }

        public SchemaMigration(
            string migrationId,
            string description,
            string checksumMaterial,
            string expectedChecksum,
            string minimumApplicationVersion,
            string rollbackCompatibility,
            bool requiresBackup,
            Action<SqliteConnection, SqliteTransaction> apply,
            Func<LegacySchemaDetector, bool> isSatisfied)
            : this(
                migrationId,
                description,
                checksumMaterial,
                expectedChecksum,
                minimumApplicationVersion,
                rollbackCompatibility,
                requiresBackup,
                apply,
                isSatisfied,
                null)
        {
        }

        public SchemaMigration(
            string migrationId,
            string description,
            string checksumMaterial,
            string expectedChecksum,
            string minimumApplicationVersion,
            string rollbackCompatibility,
            bool requiresBackup,
            Action<SqliteConnection, SqliteTransaction> apply,
            Func<LegacySchemaDetector, bool> isSatisfied,
            Func<LegacySchemaDetector, bool> recognizesLedgerlessBaseline)
        {
            if (string.IsNullOrWhiteSpace(migrationId) ||
                !MigrationIdPattern.IsMatch(migrationId))
            {
                throw new ArgumentException(
                    "Migration IDs must use the stable NNNN-semantic-name format.",
                    nameof(migrationId));
            }
            if (string.IsNullOrWhiteSpace(description))
                throw new ArgumentException("Migration description is required.", nameof(description));
            if (string.IsNullOrWhiteSpace(checksumMaterial))
                throw new ArgumentException("Migration checksum material is required.", nameof(checksumMaterial));
            if (string.IsNullOrWhiteSpace(minimumApplicationVersion))
                throw new ArgumentException("Minimum application version is required.", nameof(minimumApplicationVersion));
            if (string.IsNullOrWhiteSpace(rollbackCompatibility))
                throw new ArgumentException("Rollback compatibility is required.", nameof(rollbackCompatibility));

            var computedChecksum = ComputeChecksum(checksumMaterial);
            if (expectedChecksum != null)
            {
                if (!Regex.IsMatch(expectedChecksum, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
                {
                    throw new ArgumentException(
                        "Expected migration checksum must be lowercase SHA-256.",
                        nameof(expectedChecksum));
                }
                if (!string.Equals(computedChecksum, expectedChecksum, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Published checksum pin does not match migration material for '" +
                        migrationId + "'. Append a new migration instead of mutating a published one.");
                }
            }

            MigrationId = migrationId;
            Description = description.Trim();
            Checksum = computedChecksum;
            MinimumApplicationVersion = minimumApplicationVersion.Trim();
            RollbackCompatibility = rollbackCompatibility.Trim();
            RequiresBackup = requiresBackup;
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _isSatisfied = isSatisfied ?? throw new ArgumentNullException(nameof(isSatisfied));
            _recognizesLedgerlessBaseline = recognizesLedgerlessBaseline;
        }

        public string MigrationId { get; }
        public string Checksum { get; }
        public string Description { get; }
        public string MinimumApplicationVersion { get; }
        public bool RequiresBackup { get; }
        public string RollbackCompatibility { get; }

        internal void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            _apply(connection, transaction);
        }

        internal bool IsSatisfied(LegacySchemaDetector detector)
        {
            return _isSatisfied(detector);
        }

        internal bool RecognizesLedgerlessBaseline(LegacySchemaDetector detector)
        {
            return _recognizesLedgerlessBaseline != null &&
                _recognizesLedgerlessBaseline(detector);
        }

        private static string ComputeChecksum(string value)
        {
            var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var item in hash)
                    builder.Append(item.ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
