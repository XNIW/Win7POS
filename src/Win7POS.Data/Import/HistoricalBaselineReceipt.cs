using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Win7POS.Core.Online;

namespace Win7POS.Data.Import
{
    // An authenticated baseline ACK is stored as its original bounded JSON,
    // not reconstructed from a status flag or a source manifest.
    internal sealed class HistoricalBaselineReceipt
    {
        internal string Snapshot { get; private set; }
        internal long Revision { get; private set; }
        internal static HistoricalBaselineReceipt Read(string json, HistoricalBaselineFlow flow)
        {
            try
            {
                var root=ReadRoot(json,65536);
                var r=Closed(root,"operation_id","kind","source_run_id","source_snapshot_id","dataset_id","revision","writer_epoch",
                    "new_active","new_inactive","duplicates","new_batches","original_evidence_level","baseline_context");
                Id(Text(r,"operation_id"));var snapshot=Text(r,"source_snapshot_id");Id(snapshot);
                if(Text(r,"kind")!="migrate"||Text(r,"source_run_id")!=flow.RunId||Text(r,"dataset_id")!=flow.ServerDatasetId||
                    Text(r,"original_evidence_level")!="legacy_attested"||Integer(Text(r,"writer_epoch"),1)!=flow.WriterEpoch)throw Invalid();
                var revision=Integer(Text(r,"revision"),1);
                foreach(var name in new[]{"new_active","new_inactive","duplicates","new_batches"})
                {
                    var item=r[name];
                    if((string)item.Attribute("type")!="number"||item.HasElements||!Regex.IsMatch(item.Value,"^(0|[1-9][0-9]{0,5})$",RegexOptions.CultureInvariant)||
                        long.Parse(item.Value,CultureInfo.InvariantCulture)>(name=="new_batches"?10000:100000))throw Invalid();
                }
                var c=Closed(r["baseline_context"],"state","source_snapshot_id","local_dataset_id","local_revision","restore_epoch","restore_lineage_id","writer_epoch");
                if(Text(c,"state")!="baseline_verified"||Text(c,"source_snapshot_id")!=snapshot||Text(c,"local_dataset_id")!=flow.LocalDatasetId||
                    Integer(Text(c,"local_revision"),0)!=flow.LocalRevision||Integer(Text(c,"restore_epoch"),0)!=flow.RestoreEpoch||
                    Text(c,"restore_lineage_id")!=flow.RestoreLineageId||Integer(Text(c,"writer_epoch"),1)!=flow.WriterEpoch)throw Invalid();
                return new HistoricalBaselineReceipt{Snapshot=snapshot,Revision=revision};
            }
            catch(Exception e) when(e is XmlException||e is ArgumentException||e is FormatException||e is OverflowException||e is InvalidOperationException)
            {throw Invalid();}
        }
        internal static XElement ReadRoot(string json,int maximumBytes)
        {
            var bytes=new UTF8Encoding(false,true).GetBytes(json??string.Empty);
            if(bytes.Length==0||bytes.Length>maximumBytes)throw Invalid();
            var quotas=new XmlDictionaryReaderQuotas{MaxDepth=8,MaxStringContentLength=maximumBytes,MaxArrayLength=64,MaxNameTableCharCount=4096};
            using var reader=JsonReaderWriterFactory.CreateJsonReader(bytes,quotas);return XElement.Load(reader);
        }
        internal static Dictionary<string,XElement> Closed(XElement root,params string[] keys)
        {
            if((string)root.Attribute("type")!="object")throw Invalid();
            var elements=root.Elements().ToArray();
            if(elements.Length!=keys.Length||elements.Any(e=>!keys.Contains(e.Name.LocalName))||elements.Select(e=>e.Name.LocalName).Distinct(StringComparer.Ordinal).Count()!=keys.Length)throw Invalid();
            return elements.ToDictionary(e=>e.Name.LocalName,StringComparer.Ordinal);
        }
        internal static string Text(Dictionary<string,XElement> fields,string key)
        {var e=fields[key];if((string)e.Attribute("type")!="string"||e.HasElements)throw Invalid();return e.Value;}
        private static long Integer(string value,long minimum)
        {if(!Regex.IsMatch(value??"","^(0|[1-9][0-9]{0,18})$",RegexOptions.CultureInvariant)||!long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var n)||n<minimum)throw Invalid();return n;}
        private static void Id(string value)
        {HistoricalReplicaProtocol.DemandId(value);if(value==Guid.Empty.ToString("D"))throw Invalid();}
        private static InvalidOperationException Invalid()=>new InvalidOperationException("history_baseline_receipt_invalid");
    }
}
