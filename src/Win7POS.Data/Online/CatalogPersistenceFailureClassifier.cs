using System;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data.Online
{
    /// <summary>
    /// Keeps provider-specific exception classification in the Data layer while
    /// callers retain a stable, provider-neutral technical failure code.
    /// </summary>
    public static class CatalogPersistenceFailureClassifier
    {
        public static bool IsSqliteFailure(Exception exception)
        {
            return exception is SqliteException;
        }
    }
}
