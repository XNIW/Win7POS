using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace Win7POS.Data.Import
{
    /// <summary>Live caller ownership for the existing historical lane.
    /// This holds no SQLite connection or transaction. Durable outcomes remain
    /// in the journal; process restart intentionally releases only this live use.</summary>
    public static class HistoricalPublicationOperationUse
    {
        private static readonly ConcurrentDictionary<string,Use> Active =
            new ConcurrentDictionary<string,Use>(StringComparer.OrdinalIgnoreCase);

        public static IDisposable Begin(SqliteConnectionFactory factory,string operation)
        {
            var use=TryBegin(factory,operation);
            return use??throw new InvalidOperationException("history_preparation_in_use");
        }

        private static IDisposable TryBegin(SqliteConnectionFactory factory,string operation)
        {
            if(factory==null)throw new ArgumentNullException(nameof(factory));
            if(!Guid.TryParse(operation,out var id))throw new ArgumentException("Invalid historical operation.",nameof(operation));
            var key=Path.GetFullPath(factory.DbPath)+"\n"+id.ToString("N");
            var use=new Use(key);
            return Active.TryAdd(key,use)?use:null;
        }

        private sealed class Use:IDisposable
        {
            private string _key;
            public Use(string key)=>_key=key;
            public void Dispose()
            {
                var key=Interlocked.Exchange(ref _key,null);
                if(key!=null)Active.TryRemove(key,out _);
            }
        }
    }
}
