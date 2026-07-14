using System;
using System.IO;
using System.Threading.Tasks;

namespace Win7POS.Data.Online
{
    public sealed class AtomicRestoreInstaller
    {
        public async Task InstallAsync(
            string validatedRestorePath,
            string liveDatabasePath,
            string rollbackDatabasePath,
            Func<Task> postSwapValidationAndCommit)
        {
            if (string.IsNullOrWhiteSpace(validatedRestorePath))
                throw new ArgumentException("Validated restore path is required.", nameof(validatedRestorePath));
            if (string.IsNullOrWhiteSpace(liveDatabasePath))
                throw new ArgumentException("Live database path is required.", nameof(liveDatabasePath));
            if (string.IsNullOrWhiteSpace(rollbackDatabasePath))
                throw new ArgumentException("Rollback database path is required.", nameof(rollbackDatabasePath));
            if (postSwapValidationAndCommit == null)
                throw new ArgumentNullException(nameof(postSwapValidationAndCommit));
            if (!File.Exists(validatedRestorePath))
                throw new FileNotFoundException("Validated restore file was not found.", validatedRestorePath);
            if (!File.Exists(rollbackDatabasePath))
                throw new FileNotFoundException("Rollback database file was not found.", rollbackDatabasePath);

            var swapAttempted = false;
            try
            {
                SqliteConnectionFactory.ClearAllPools();
                swapAttempted = true;
                DeleteSqliteSidecars(liveDatabasePath);
                File.Copy(validatedRestorePath, liveDatabasePath, true);
                SqliteConnectionFactory.ClearAllPools();
                await postSwapValidationAndCommit().ConfigureAwait(false);
            }
            catch (Exception installException)
            {
                try
                {
                    if (swapAttempted)
                    {
                        SqliteConnectionFactory.ClearAllPools();
                        DeleteSqliteSidecars(liveDatabasePath);
                        File.Copy(rollbackDatabasePath, liveDatabasePath, true);
                        SqliteConnectionFactory.ClearAllPools();
                    }
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Restore failed and the pre-restore database could not be reinstated.",
                        installException,
                        rollbackException);
                }

                throw;
            }
        }

        private static void DeleteSqliteSidecars(string databasePath)
        {
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
        }
    }
}
