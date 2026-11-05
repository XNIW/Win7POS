using System;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data.Import
{
    // Test assembly friends only; production composition cannot enable failpoints.
    internal sealed class HistoricalSalesImportTestHooks
    {
        internal Action<string, SqliteConnection, SqliteTransaction> AtPhase { get; set; }
    }
}
