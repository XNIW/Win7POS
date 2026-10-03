using System;
using System.Collections.Generic;
using System.Linq;

namespace Win7POS.Data.Repositories
{
    public sealed class SettingsAuditEntry
    {
        public long Id { get; set; }
        public string Event { get; set; }
        public string Actor { get; set; }
        public string Source { get; set; }
        public string KeyNames { get; set; }
        public int KeyCount { get; set; }
        public string BeforeHash { get; set; }
        public string AfterHash { get; set; }
        public string Result { get; set; }
        public string CreatedUtc { get; set; }
    }
    public sealed class SettingsCommittedEventArgs : EventArgs
    {
        public string DatabasePath { get; }
        public IReadOnlyList<string> Keys { get; }
        internal SettingsCommittedEventArgs(string databasePath, IEnumerable<string> keys)
        {
            DatabasePath = databasePath;
            Keys = keys.ToArray();
        }
    }
}
