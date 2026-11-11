using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Online;
using Win7POS.Data.Online;
using Win7POS.Data.Reports;

namespace Win7POS.Data.Import
{
    public sealed class HistoricalReplicaState
    {
        public string ShopId { get; set; } public string DatasetId { get; set; } public long WriterEpoch { get; set; }
        public long ServerRevision { get; set; } public string WriterAuthority { get; set; } public string GenerationId { get; set; }
        public long? PublishedRevision { get; set; } public string Checksum { get; set; } public long? LastSuccessUtcMs { get; set; }
        public string StaleReason { get; set; } public int EverWeb { get; set; } public int CanonicalReadEnabled { get; set; }
    }
    public sealed class HistoricalReplicaStore
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly Action _demandCurrent;
        private readonly OnlineSyncGeneration _generation;
        public HistoricalReplicaStore(SqliteConnectionFactory factory,Action demandCurrent,OnlineSyncGeneration generation=null)
        { _factory=factory??throw new ArgumentNullException(nameof(factory));_demandCurrent=demandCurrent??throw new ArgumentNullException(nameof(demandCurrent));_generation=generation; }
        private async Task DemandCommitAsync(SqliteConnection c,SqliteTransaction t,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();_demandCurrent();
            if(_generation!=null&&!await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(c,t,_generation).ConfigureAwait(false))
                throw new OnlineSyncGenerationChangedException();
        }
        private async Task CommitReportChangeAsync(SqliteConnection c,SqliteTransaction t,CancellationToken ct)
        {
            var coordinator=SalesReportSnapshotCoordinator.ForDatabase(_factory.DbPath);
            using(await coordinator.EnterPublicationAsync(ct).ConfigureAwait(false))
            {
                await DemandCommitAsync(c,t,ct).ConfigureAwait(false);
                coordinator.CommitAndPublishReportRevision(c,t,t.Commit,_factory.AuthorityStamp);
            }
        }
        private static CommandDefinition Command(string sql,object p,SqliteTransaction t,CancellationToken ct)=>new CommandDefinition(sql,p,t,cancellationToken:ct);
        public async Task<(string GenerationId,HistoricalCloudResult Manifest,bool Complete)> ReadPendingAsync(string shop,CancellationToken ct)
        {
            await PruneAsync(shop,ct).ConfigureAwait(false);
            _demandCurrent();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);
            var g=await c.QuerySingleOrDefaultAsync<Generation>(Command(@"SELECT g.generation_id Id,g.shop_id Shop,g.dataset_id DatasetId,g.writer_epoch Epoch,g.revision Revision,g.mode Mode,g.checksum Checksum,g.movement_count Movements,g.batch_count Batches,g.next_cursor NextCursor,g.terminal_cursor TerminalCursor,g.complete Complete
FROM historical_cloud_generation g JOIN historical_cloud_state s ON s.shop_id=g.shop_id AND s.dataset_id=g.dataset_id AND s.writer_epoch=g.writer_epoch
WHERE g.shop_id=@shop AND g.generation_id IS NOT s.generation_id AND g.source_generation_id IS s.generation_id AND g.created_utc_ms>@oldest
ORDER BY g.created_utc_ms DESC,g.generation_id DESC LIMIT 1",new{shop,oldest=DateTimeOffset.UtcNow.AddMinutes(-55).ToUnixTimeMilliseconds()},null,ct)).ConfigureAwait(false);
            _demandCurrent();return g==null?(null,null,false):(g.Id,new HistoricalCloudResult{DatasetId=g.DatasetId,Revision=g.Revision.ToString(CultureInfo.InvariantCulture),WriterEpoch=g.Epoch.ToString(CultureInfo.InvariantCulture),Mode=g.Mode,Checksum=g.Checksum,Movements=g.Movements,Batches=g.Batches,Cursor=g.Complete==1?g.TerminalCursor:g.NextCursor},g.Complete==1);
        }
        public async Task<HistoricalReplicaState> ReadAsync(string shop,CancellationToken ct)
        {
            HistoricalReplicaProtocol.DemandId(shop);_demandCurrent();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);
            var state=await c.QuerySingleOrDefaultAsync<HistoricalReplicaState>(Command("SELECT shop_id ShopId,dataset_id DatasetId,writer_epoch WriterEpoch,server_revision ServerRevision,writer_authority WriterAuthority,generation_id GenerationId,published_revision PublishedRevision,checksum Checksum,last_success_utc_ms LastSuccessUtcMs,stale_reason StaleReason,ever_web EverWeb,CASE WHEN s.ever_web=1 OR EXISTS("+HistoricalBaselineReadMode.LocalScope+") THEN 1 ELSE 0 END CanonicalReadEnabled FROM historical_cloud_state s WHERE shop_id=@shop",new{shop},null,ct)).ConfigureAwait(false);_demandCurrent();return state;
        }
        public async Task ObserveAuthorityAsync(string shop,HistoricalCloudResult authority,CancellationToken ct)
        {
            HistoricalReplicaProtocol.DemandId(shop);HistoricalReplicaProtocol.DemandId(authority?.DatasetId);
            var revision=HistoricalReplicaProtocol.Integer(authority.Revision);var epoch=HistoricalReplicaProtocol.Integer(authority.WriterEpoch);
            if(revision<0||epoch<1||authority.ProtocolVersion!=1||!(new[]{"local","frozen","web"}).Contains(authority.WriterAuthority))throw new InvalidOperationException("history_cloud_authority_invalid");
            _demandCurrent();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            var old=await c.QuerySingleOrDefaultAsync<AuthorityRow>(Command("SELECT dataset_id DatasetId,writer_epoch Epoch,writer_authority Authority,server_revision ServerRevision FROM historical_cloud_state WHERE shop_id=@shop",new{shop},t,ct)).ConfigureAwait(false);
            if(old!=null&&old.DatasetId==authority.DatasetId&&epoch<old.Epoch)throw new InvalidOperationException("history_cloud_epoch_regressed");
            if(old!=null&&old.DatasetId==authority.DatasetId&&epoch==old.Epoch&&revision<old.ServerRevision)throw new InvalidOperationException("history_cloud_revision_regressed");
            await c.ExecuteAsync(Command(@"INSERT INTO historical_cloud_state(shop_id,dataset_id,writer_epoch,server_revision,writer_authority,ever_web) VALUES(@shop,@dataset,@epoch,@revision,@writer,@web)
ON CONFLICT(shop_id) DO UPDATE SET generation_id=CASE WHEN dataset_id=@dataset THEN generation_id END,published_revision=CASE WHEN dataset_id=@dataset THEN published_revision END,checksum=CASE WHEN dataset_id=@dataset THEN checksum END,
dataset_id=@dataset,writer_epoch=@epoch,server_revision=@revision,writer_authority=@writer,ever_web=MAX(ever_web,@web);",new{shop,dataset=authority.DatasetId,epoch,revision,writer=authority.WriterAuthority,web=authority.WriterAuthority=="web"?1:0},t,ct)).ConfigureAwait(false);
            if(old==null||old.DatasetId!=authority.DatasetId||old.Epoch!=epoch||old.Authority!=authority.WriterAuthority)
            {
                await Bump(c,t,ct).ConfigureAwait(false);
                await CommitReportChangeAsync(c,t,ct).ConfigureAwait(false);
            }
            else
            {
                await DemandCommitAsync(c,t,ct).ConfigureAwait(false);t.Commit();
                if(old.ServerRevision!=revision)SalesReportSnapshotCoordinator.ForDatabase(_factory.DbPath).NotifyHistoricalSyncStatusCommitted();
            }
        }
        public async Task<string> BeginAsync(string shop,HistoricalCloudResult manifest,CancellationToken ct)
        {
            HistoricalReplicaProtocol.DemandId(shop);HistoricalReplicaProtocol.DemandId(manifest?.DatasetId);HistoricalReplicaProtocol.DemandId(manifest.Cursor);HistoricalReplicaProtocol.DemandHash(manifest.Checksum);
            var revision=HistoricalReplicaProtocol.Integer(manifest.Revision);var epoch=HistoricalReplicaProtocol.Integer(manifest.WriterEpoch);
            if(revision<0||epoch<1||manifest.Movements<0||manifest.Movements>1000000||manifest.Batches<0||manifest.Batches>100000||manifest.Mode!="snapshot"&&manifest.Mode!="delta")throw new InvalidOperationException("history_replica_manifest_invalid");
            _demandCurrent();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            var state=await c.QuerySingleAsync<GenerationSource>(Command("SELECT dataset_id DatasetId,writer_epoch Epoch,generation_id GenerationId,published_revision Revision FROM historical_cloud_state WHERE shop_id=@shop",new{shop},t,ct)).ConfigureAwait(false);
            if(state.DatasetId!=manifest.DatasetId||state.Epoch!=epoch||manifest.Mode=="delta"&&(state.GenerationId==null||state.Revision>revision))throw new InvalidOperationException("history_replica_resnapshot_required");
            await Prune(c,t,shop,ct).ConfigureAwait(false);
            if(await c.ExecuteScalarAsync<long>(Command("SELECT COUNT(*) FROM historical_cloud_generation WHERE shop_id=@shop AND generation_id IS NOT @published",new{shop,published=state.GenerationId},t,ct)).ConfigureAwait(false)>=3)throw new InvalidOperationException("history_replica_transfer_capacity");
            var generation=Guid.NewGuid().ToString("D");
            await c.ExecuteAsync(Command(@"INSERT INTO historical_cloud_generation(generation_id,shop_id,dataset_id,writer_epoch,revision,mode,checksum,movement_count,batch_count,next_cursor,source_generation_id,created_utc_ms)
VALUES(@generation,@shop,@dataset,@epoch,@revision,@mode,@checksum,@movements,@batches,@cursor,@source,@utc)",new{generation,shop,dataset=manifest.DatasetId,epoch,revision,mode=manifest.Mode,checksum=manifest.Checksum,movements=manifest.Movements,batches=manifest.Batches,cursor=manifest.Cursor,source=state.GenerationId,utc=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},t,ct)).ConfigureAwait(false);
            await DemandCommitAsync(c,t,ct).ConfigureAwait(false);t.Commit();return generation;
        }
        public async Task StageAsync(string generation,HistoricalCloudResult page,CancellationToken ct)
        {
            HistoricalReplicaProtocol.DemandId(generation);HistoricalReplicaProtocol.DemandId(page?.Cursor);if(page.NextCursor!=null)HistoricalReplicaProtocol.DemandId(page.NextCursor);
            if(page.Entities==null||page.Entities.Length>1000||page.Done!=(page.NextCursor==null))throw new InvalidOperationException("history_replica_page_invalid");
            _demandCurrent();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            var g=await LoadGeneration(c,t,generation,ct).ConfigureAwait(false);var json=Serialize(page);var digest=Hash(json);
            if(g.LastCursor==page.Cursor){if(g.LastPageDigest!=digest)throw new InvalidOperationException("history_replica_page_changed");return;}
            if(g.Complete!=0||g.NextCursor!=page.Cursor||g.DatasetId!=page.DatasetId||g.Revision.ToString(CultureInfo.InvariantCulture)!=page.Revision||g.Epoch.ToString(CultureInfo.InvariantCulture)!=page.WriterEpoch||g.Checksum!=page.Checksum||g.Movements!=page.Movements||g.Batches!=page.Batches)throw new InvalidOperationException("history_replica_page_changed");
            // Materialize only when a delta actually changes entities. Empty pages retain the source.
            if(g.Mode=="delta"&&page.Entities.Length>0&&await c.ExecuteScalarAsync<long>(Command("SELECT EXISTS(SELECT 1 FROM historical_cloud_entity WHERE generation_id=@generation)",new{generation},t,ct)).ConfigureAwait(false)==0)await c.ExecuteAsync(Command(@"INSERT INTO historical_cloud_entity(generation_id,entity_kind,entity_id,entity_digest,payload_json,shop_id,batch_id,identity_key,is_active,business_ticks,signed_amount_clp,signed_fee_clp,signed_fee_vat_clp,signed_net_clp,external_namespace,external_id,source_sheet,source_row,timestamp_raw,offset_minutes,card_subtype)
SELECT @generation,entity_kind,entity_id,entity_digest,payload_json,shop_id,batch_id,identity_key,is_active,business_ticks,signed_amount_clp,signed_fee_clp,signed_fee_vat_clp,signed_net_clp,external_namespace,external_id,source_sheet,source_row,timestamp_raw,offset_minutes,card_subtype FROM historical_cloud_entity WHERE generation_id=@source",new{generation,source=g.SourceGeneration},t,ct)).ConfigureAwait(false);
            foreach(var entity in page.Entities)
            {
                ct.ThrowIfCancellationRequested();HistoricalReplicaProtocol.Validate(entity,g.Revision);var p=entity.Payload;var movement=entity.Kind=="movement";
                if(g.Mode=="snapshot"&&await c.ExecuteScalarAsync<long>(Command("SELECT COUNT(*) FROM historical_cloud_entity WHERE generation_id=@generation AND entity_kind=@kind AND entity_id=@id",new{generation,kind=entity.Kind,id=entity.Id},t,ct)).ConfigureAwait(false)!=0)throw new InvalidOperationException("history_replica_duplicate_entity");
                await c.ExecuteAsync(Command(@"INSERT INTO historical_cloud_entity(generation_id,entity_kind,entity_id,entity_digest,payload_json,shop_id,batch_id,identity_key,is_active,business_ticks,signed_amount_clp,signed_fee_clp,signed_fee_vat_clp,signed_net_clp,external_namespace,external_id,source_sheet,source_row,timestamp_raw,offset_minutes,card_subtype)
VALUES(@generation,@kind,@id,@digest,@json,@shop,@batch,@identity,@active,@ticks,@gross,@fee,@vat,@net,@ns,@external,@sheet,@row,@raw,@offset,@card)
ON CONFLICT(generation_id,entity_kind,entity_id) DO UPDATE SET entity_digest=excluded.entity_digest,payload_json=excluded.payload_json,batch_id=excluded.batch_id,identity_key=excluded.identity_key,is_active=excluded.is_active,business_ticks=excluded.business_ticks,signed_amount_clp=excluded.signed_amount_clp,signed_fee_clp=excluded.signed_fee_clp,signed_fee_vat_clp=excluded.signed_fee_vat_clp,signed_net_clp=excluded.signed_net_clp,external_namespace=excluded.external_namespace,external_id=excluded.external_id,source_sheet=excluded.source_sheet,source_row=excluded.source_row,timestamp_raw=excluded.timestamp_raw,offset_minutes=excluded.offset_minutes,card_subtype=excluded.card_subtype;",
                    new{generation,kind=entity.Kind,id=entity.Id,digest=entity.Digest,json=Serialize(entity),shop=g.Shop,batch=p.BatchId,identity=p.IdentityKey,active=movement?(p.Active.Value?1:0):(int?)null,ticks=movement?HistoricalReplicaProtocol.Integer(p.BusinessTicks):(long?)null,gross=movement?HistoricalReplicaProtocol.Integer(p.GrossClp):(long?)null,fee=NullableInteger(p.FeeClp),vat=NullableInteger(p.FeeVatClp),net=NullableInteger(p.NetClp),ns=p.ExternalNamespace,external=p.ExternalId,sheet=p.SourceSheet,row=NullableInteger(p.SourceRow),raw=p.TimestampRaw,offset=NullableInteger(p.OffsetMinutes),card=p.CardSubtype},t,ct)).ConfigureAwait(false);
            }
            await c.ExecuteAsync(Command("UPDATE historical_cloud_generation SET next_cursor=@next,terminal_cursor=@terminal,complete=@complete,last_cursor=@cursor,last_page_digest=@digest WHERE generation_id=@generation",new{generation,next=page.NextCursor,terminal=page.Done?page.Cursor:null,complete=page.Done?1:0,cursor=page.Cursor,digest},t,ct)).ConfigureAwait(false);
            await DemandCommitAsync(c,t,ct).ConfigureAwait(false);t.Commit();
        }
        private async Task<VerifiedPublication> VerifyPublicationAsync(string generation,CancellationToken ct)
        {
            _demandCurrent();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction(deferred:true);var g=await LoadGeneration(c,t,generation,ct).ConfigureAwait(false);
            if(g.Complete!=1||g.TerminalCursor==null)throw new InvalidOperationException("history_replica_incomplete");
            var emptyDelta=g.Mode=="delta"&&g.SourceGeneration!=null&&await c.ExecuteScalarAsync<long>(Command("SELECT EXISTS(SELECT 1 FROM historical_cloud_entity WHERE generation_id=@generation)",new{generation},t,ct)).ConfigureAwait(false)==0;
            var published=emptyDelta?g.SourceGeneration:generation;
            var counts=await c.QuerySingleAsync<Counts>(Command("SELECT SUM(CASE WHEN entity_kind='movement' THEN 1 ELSE 0 END) Movements,SUM(CASE WHEN entity_kind='batch' THEN 1 ELSE 0 END) Batches FROM historical_cloud_entity WHERE generation_id=@generation",new{generation=published},t,ct)).ConfigureAwait(false);
            if(counts.Movements!=g.Movements||counts.Batches!=g.Batches||await c.ExecuteScalarAsync<long>(Command(@"SELECT COUNT(*) FROM (SELECT identity_key FROM historical_cloud_entity WHERE generation_id=@generation AND entity_kind='movement' AND is_active=1 GROUP BY identity_key HAVING COUNT(*)>1)",new{generation=published},t,ct)).ConfigureAwait(false)!=0)throw new InvalidOperationException("history_replica_count_mismatch");
            if(await c.ExecuteScalarAsync<long>(Command(@"SELECT COUNT(*) FROM historical_cloud_entity m WHERE m.generation_id=@generation AND m.entity_kind='movement' AND NOT EXISTS(SELECT 1 FROM historical_cloud_entity b WHERE b.generation_id=m.generation_id AND b.entity_kind='batch' AND b.entity_id=m.batch_id)",new{generation=published},t,ct)).ConfigureAwait(false)!=0)throw new InvalidOperationException("history_replica_batch_missing");
            var entities=c.Query<string>("SELECT payload_json FROM historical_cloud_entity WHERE generation_id=@generation ORDER BY entity_kind COLLATE BINARY,entity_id COLLATE BINARY",new{generation=published},t,buffered:false)
                .Select(json=>{ct.ThrowIfCancellationRequested();return Deserialize<HistoricalReplicaEntity>(json);});
            var checksum=HistoricalReplicaProtocol.SnapshotChecksumOrdered(g.Shop,g.DatasetId,g.Revision,g.Epoch,entities);
            if(checksum!=g.Checksum)throw new InvalidOperationException("history_replica_checksum_mismatch");
            t.Commit();_demandCurrent();return new VerifiedPublication{Generation=g,Published=published,EmptyDelta=emptyDelta};
        }
        public async Task<string> PublishAsync(string generation,CancellationToken ct)
        {
            // Verify the immutable complete candidate in a WAL read snapshot.
            // Checksumming a large generation must not reserve SQLite's writer.
            var verified=await VerifyPublicationAsync(generation,ct).ConfigureAwait(false);
            var g=verified.Generation;var published=verified.Published;var emptyDelta=verified.EmptyDelta;var checksum=g.Checksum;
            using(var c=await _factory.OpenAsync(ct).ConfigureAwait(false))
            {
                using var t=c.BeginTransaction();
                var current=await LoadGeneration(c,t,generation,ct).ConfigureAwait(false);
                if(current.Complete!=1||current.TerminalCursor!=g.TerminalCursor||current.SourceGeneration!=g.SourceGeneration||
                    current.DatasetId!=g.DatasetId||current.Epoch!=g.Epoch||current.Revision!=g.Revision||current.Checksum!=g.Checksum||
                    current.Mode!=g.Mode||current.Movements!=g.Movements||current.Batches!=g.Batches)
                    throw new InvalidOperationException("history_replica_publication_changed");
                var prior=await c.QuerySingleAsync<PublishedState>(Command("SELECT generation_id GenerationId,published_revision Revision,stale_reason StaleReason FROM historical_cloud_state WHERE shop_id=@shop",new{shop=g.Shop},t,ct)).ConfigureAwait(false);
                var changed=await c.ExecuteAsync(Command(@"UPDATE historical_cloud_state SET generation_id=@generation,published_revision=@revision,server_revision=MAX(server_revision,@revision),checksum=@checksum,last_success_utc_ms=@utc,stale_reason=NULL
    WHERE shop_id=@shop AND dataset_id=@dataset AND writer_epoch=@epoch AND generation_id IS @source AND (published_revision IS NULL OR published_revision<=@revision)",new{generation=published,revision=g.Revision,checksum,utc=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),shop=g.Shop,dataset=g.DatasetId,epoch=g.Epoch,source=g.SourceGeneration},t,ct)).ConfigureAwait(false);
                if(changed!=1)throw new InvalidOperationException("history_replica_publication_changed");
                var canonicalReadChanged=HistoricalBaselineReadMode.TryActivate(c,t,g.Shop,_factory.AuthorityStamp.RestoreEpoch)>0;
                // The empty candidate carries only the resumable cursor; no full copy survives publication.
                if(emptyDelta)await c.ExecuteAsync(Command("DELETE FROM historical_cloud_generation WHERE generation_id=@generation",new{generation},t,ct)).ConfigureAwait(false);
                // Reclaim obsolete generations only after pointer publication has
                // committed, in the existing cleanup's separate transaction.
                if(canonicalReadChanged||prior.GenerationId!=published||prior.Revision!=g.Revision||prior.StaleReason!=null)
                {
                    await Bump(c,t,ct).ConfigureAwait(false);
                    await CommitReportChangeAsync(c,t,ct).ConfigureAwait(false);
                }
                else
                {
                    await DemandCommitAsync(c,t,ct).ConfigureAwait(false);t.Commit();
                    SalesReportSnapshotCoordinator.ForDatabase(_factory.DbPath).NotifyHistoricalSyncStatusCommitted();
                }
            }
            await PruneAsync(g.Shop,ct).ConfigureAwait(false);
            return g.TerminalCursor;
        }
        public async Task MarkStaleAsync(string shop,CancellationToken ct)
        {
            _demandCurrent();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            if(await c.ExecuteAsync(Command("UPDATE historical_cloud_state SET stale_reason='offline_or_interrupted' WHERE shop_id=@shop AND stale_reason IS NOT 'offline_or_interrupted'",new{shop},t,ct)).ConfigureAwait(false)>0)
            {
                await Bump(c,t,ct).ConfigureAwait(false);
                await CommitReportChangeAsync(c,t,ct).ConfigureAwait(false);
            }
            else { await DemandCommitAsync(c,t,ct).ConfigureAwait(false);t.Commit(); }
        }
        public async Task PruneAsync(string shop,CancellationToken ct)
        {
            HistoricalReplicaProtocol.DemandId(shop);_demandCurrent();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            await Prune(c,t,shop,ct).ConfigureAwait(false);await DemandCommitAsync(c,t,ct).ConfigureAwait(false);t.Commit();
        }
        private static async Task Prune(SqliteConnection c,SqliteTransaction t,string shop,CancellationToken ct)
        {
            // Keep the published copy and every compatible transfer within the resume lease.
            // At most three such transfers can be admitted. Source generations of stale
            // candidates cannot publish through the compare-and-swap and may be reclaimed.
            const string obsolete=@"SELECT g.generation_id FROM historical_cloud_generation g WHERE g.shop_id=@shop
AND NOT EXISTS(SELECT 1 FROM historical_cloud_state s WHERE s.shop_id=g.shop_id AND (s.generation_id=g.generation_id OR
(s.dataset_id=g.dataset_id AND s.writer_epoch=g.writer_epoch AND g.source_generation_id IS s.generation_id AND g.revision>=COALESCE(s.published_revision,0) AND g.created_utc_ms>@oldest)))";
            var args=new{shop,oldest=DateTimeOffset.UtcNow.AddMinutes(-55).ToUnixTimeMilliseconds()};
            await c.ExecuteAsync(Command("DELETE FROM historical_cloud_entity WHERE generation_id IN ("+obsolete+")",args,t,ct)).ConfigureAwait(false);
            await c.ExecuteAsync(Command("DELETE FROM historical_cloud_generation WHERE generation_id IN ("+obsolete+")",args,t,ct)).ConfigureAwait(false);
        }
        private static long? NullableInteger(string value)=>value==null?(long?)null:HistoricalReplicaProtocol.Integer(value);
        private static Task<int> Bump(SqliteConnection c,SqliteTransaction t,CancellationToken ct)=>c.ExecuteAsync(Command("UPDATE historical_cloud_cache_revision SET revision=revision+1 WHERE singleton_id=1",null,t,ct));
        private static Task<Generation> LoadGeneration(SqliteConnection c,SqliteTransaction t,string generation,CancellationToken ct)=>c.QuerySingleAsync<Generation>(Command("SELECT generation_id Id,shop_id Shop,dataset_id DatasetId,writer_epoch Epoch,revision Revision,mode Mode,checksum Checksum,movement_count Movements,batch_count Batches,next_cursor NextCursor,terminal_cursor TerminalCursor,source_generation_id SourceGeneration,complete Complete,last_cursor LastCursor,last_page_digest LastPageDigest FROM historical_cloud_generation WHERE generation_id=@generation",new{generation},t,ct));
        private static string Hash(string value){using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant();}
        private static string Serialize<T>(T value){using var stream=new MemoryStream();new DataContractJsonSerializer(typeof(T)).WriteObject(stream,value);return Encoding.UTF8.GetString(stream.ToArray());}
        private static T Deserialize<T>(string value){using var stream=new MemoryStream(Encoding.UTF8.GetBytes(value));return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);}
        private sealed class AuthorityRow{public string DatasetId{get;set;}public long Epoch{get;set;}public string Authority{get;set;}public long ServerRevision{get;set;}}
        private sealed class PublishedState{public string GenerationId{get;set;}public long? Revision{get;set;}public string StaleReason{get;set;}}
        private sealed class VerifiedPublication{public Generation Generation{get;set;}public string Published{get;set;}public bool EmptyDelta{get;set;}}
        private sealed class GenerationSource{public string DatasetId{get;set;}public long Epoch{get;set;}public string GenerationId{get;set;}public long? Revision{get;set;}}
        private sealed class Counts{public long Movements{get;set;}public long Batches{get;set;}}
        private sealed class Generation{public string Id{get;set;}public string Shop{get;set;}public string DatasetId{get;set;}public long Epoch{get;set;}public long Revision{get;set;}public string Mode{get;set;}public string Checksum{get;set;}public int Movements{get;set;}public int Batches{get;set;}public string NextCursor{get;set;}public string TerminalCursor{get;set;}public string SourceGeneration{get;set;}public int Complete{get;set;}public string LastCursor{get;set;}public string LastPageDigest{get;set;}}
    }
}
