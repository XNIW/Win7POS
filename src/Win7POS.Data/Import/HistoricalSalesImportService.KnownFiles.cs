using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Import;

namespace Win7POS.Data.Import
{
    public sealed partial class HistoricalSalesImportService
    {
        // Recognition is read-only. Neither a matching name nor a terminal
        // receipt proves that its movements are still active after an Undo.
        public async Task<bool> AreFilesAlreadyActiveAsync(string shopId, HistoricalSalesImportAnalysis analysis, CancellationToken ct)
        {
            HistoricalMerchantAssignment.Required(shopId);
            if(analysis==null||analysis.HasErrors||analysis.Files.Count==0||analysis.Files.Count>10||analysis.AcceptedTransactions.Count==0)return false;
            var stamp=_authority();
            using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);
            using var tx=c.BeginTransaction(deferred:true);
            if(await c.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM historical_cloud_state WHERE shop_id=@shopId AND (ever_web=1 OR writer_authority<>'local')",new{shopId},tx,cancellationToken:ct)).ConfigureAwait(false)!=0)return false;
            foreach(var file in analysis.Files)
            {
                ct.ThrowIfCancellationRequested();
                var accepted=new HashSet<int>(analysis.AcceptedTransactions.Where(r=>r.Source.FileId==file.FileId).Select(r=>r.Source.RowNumber));
                var expected=analysis.AcceptedTransactions.Where(r=>r.Source.FileId==file.FileId).ToDictionary(r=>r.Source.RowNumber);
                var rejected=new HashSet<int>(analysis.RejectedRows.Where(r=>r.Source.FileId==file.FileId).Select(r=>r.Source.RowNumber));
                if(!file.Profile.HasValue||accepted.Overlaps(rejected)||accepted.Count+rejected.Count!=file.SourceRows)return false;
                var ids=(await c.QueryAsync<string>(new CommandDefinition(@"SELECT f.id FROM historical_import_file f
JOIN historical_import_batch b ON b.id=f.batch_id AND b.shop_id=@shopId AND b.state='Active'
JOIN historical_import_dataset d ON d.singleton_id=1 AND d.dataset_id=b.dataset_id
JOIN historical_import_operation o ON o.operation_id=b.apply_operation_id AND o.batch_id=b.id AND o.shop_id=b.shop_id AND o.dataset_id=b.dataset_id AND o.operation_kind='Apply'
WHERE f.source_sha256=@Sha256 AND f.profile=@profile AND f.parser_version=@parser AND f.source_rows=@SourceRows
ORDER BY b.created_at DESC,f.id LIMIT 32",new{shopId,file.Sha256,profile=file.Profile.Value.ToString(),parser=HistoricalQualificationResolver.ParserVersion,file.SourceRows},tx,cancellationToken:ct)).ConfigureAwait(false)).ToArray();
                var found=false;
                foreach(var id in ids)
                {
                    var active=(await c.QueryAsync<KnownFileMovement>(new CommandDefinition(@"SELECT t.source_row AS SourceRow,t.external_namespace AS ExternalNamespace,t.external_id AS ExternalId,t.timestamp_raw AS TimestampRaw,t.offset_minutes AS OffsetMinutes,
t.movement_kind AS MovementKind,t.state AS State,t.settlement_state AS SettlementState,t.signed_amount_clp AS Gross,t.signed_fee_clp AS Fee,t.signed_fee_vat_clp AS Vat,t.signed_net_clp AS Net,t.card_subtype AS CardSubtype
FROM historical_transactions t WHERE t.file_id=@id AND t.shop_id=@shopId AND t.is_active=1
UNION ALL SELECT o.source_row,t.external_namespace,t.external_id,t.timestamp_raw,t.offset_minutes,t.movement_kind,t.state,t.settlement_state,t.signed_amount_clp,t.signed_fee_clp,t.signed_fee_vat_clp,t.signed_net_clp,t.card_subtype FROM historical_import_observation o
JOIN historical_transactions t ON t.id=o.existing_transaction_id AND t.shop_id=@shopId AND t.is_active=1
JOIN historical_import_batch b ON b.id=t.batch_id AND b.shop_id=t.shop_id AND b.state='Active'
WHERE o.file_id=@id AND o.observation_kind='Duplicate' AND o.code='same_economic_identity'",new{id,shopId},tx,cancellationToken:ct)).ConfigureAwait(false)).ToArray();
                    if(active.Length!=accepted.Count||!accepted.SetEquals(active.Select(r=>r.SourceRow))||active.Any(r=>!r.Matches(expected[r.SourceRow])))continue;
                    var excluded=await c.QueryAsync<int>(new CommandDefinition("SELECT source_row FROM historical_import_observation WHERE file_id=@id AND observation_kind='Rejected' AND code='provider_rejected'",new{id},tx,cancellationToken:ct)).ConfigureAwait(false);
                    if(rejected.SetEquals(excluded)){found=true;break;}
                }
                if(!found)return false;
            }
            ct.ThrowIfCancellationRequested();var current=_authority();
            if(current.RestoreEpoch!=stamp.RestoreEpoch||current.AuthorityInstanceEpoch!=stamp.AuthorityInstanceEpoch)throw new InvalidOperationException("historical_read_scope_changed");
            tx.Commit();return true;
        }
        private sealed class KnownFileMovement
        {
            public int SourceRow{get;set;} public string ExternalNamespace{get;set;} public string ExternalId{get;set;}
            public string TimestampRaw{get;set;} public int? OffsetMinutes{get;set;} public string MovementKind{get;set;}
            public string State{get;set;} public string SettlementState{get;set;} public long Gross{get;set;}
            public long? Fee{get;set;} public long? Vat{get;set;} public long? Net{get;set;} public string CardSubtype{get;set;}
            internal bool Matches(HistoricalSalesTransaction r)=>ExternalNamespace==r.ExternalNamespace&&ExternalId==r.ExternalId&&TimestampRaw==r.Timestamp.RawTimestamp&&
                OffsetMinutes==r.Timestamp.OffsetMinutes&&MovementKind==r.MovementKind.ToString()&&State==r.TransactionStatus&&SettlementState==r.SettlementStatus&&
                Gross==r.GrossClp&&Fee==r.CommissionClp&&Vat==r.CommissionVatClp&&Net==r.NetClp&&CardSubtype==r.CardSubtype.ToString();
        }
    }
}
