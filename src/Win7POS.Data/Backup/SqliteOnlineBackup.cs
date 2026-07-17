using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Backup
{
    public sealed class SqliteOnlineBackup
    {
        private readonly SqliteConnectionFactory _sourceFactory;

        public SqliteOnlineBackup(SqliteConnectionFactory sourceFactory)
        {
            _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        }

        public Task<DatabaseValidationResult> CreateVerifiedAsync(string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("Backup destination path is required.", nameof(destinationPath));

            return Task.Run(() => CreateVerified(destinationPath));
        }

        private DatabaseValidationResult CreateVerified(string destinationPath)
        {
            var sourcePath = Path.GetFullPath(_sourceFactory.DbPath);
            var finalPath = Path.GetFullPath(destinationPath);
            if (string.Equals(sourcePath, finalPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Backup destination must differ from the live database path.");
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Current POS database not found.", sourcePath);
            if (File.Exists(finalPath))
                throw new IOException("Backup destination already exists: " + finalPath);

            var directory = Path.GetDirectoryName(finalPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Backup destination directory is invalid.");
            Directory.CreateDirectory(directory);

            var temporaryPath = finalPath + ".partial-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                using (var source = _sourceFactory.Open())
                using (var destination = new SqliteConnection(BuildDestinationConnectionString(temporaryPath)))
                {
                    destination.Open();
                    source.BackupDatabase(destination);
                }

                var validationFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(temporaryPath));
                var validation = new DbMaintenanceRepository(validationFactory)
                    .ValidateAsync()
                    .GetAwaiter()
                    .GetResult();
                if (!validation.IsValid)
                {
                    throw new InvalidDataException(
                        "SQLite online backup validation failed. integrity=" + validation.IntegrityCheck +
                        " foreignKeys=" + validation.ForeignKeyCheck);
                }

                SqliteConnectionFactory.ClearAllPools();
                File.Move(temporaryPath, finalPath);
                return validation;
            }
            catch
            {
                SqliteConnectionFactory.ClearAllPools();
                DeleteIfPresent(temporaryPath);
                DeleteIfPresent(temporaryPath + "-wal");
                DeleteIfPresent(temporaryPath + "-shm");
                throw;
            }
        }

        private static string BuildDestinationConnectionString(string path)
        {
            return new SqliteConnectionStringBuilder
            {
                Cache = SqliteCacheMode.Private,
                DataSource = path,
                ForeignKeys = true,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
