using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static partial class HistoricalSalesLateReportProbe
    {
        internal static async Task<int> RunCloudProbeAsync(string[] args)
        {
            string Arg(string key){var i=Array.IndexOf(args,key);if(i<0||i+1>=args.Length)throw new ArgumentException("qa_history_argument_missing");return args[i+1];}
            var receipt=new HistoricalSalesQaReceipt(Arg("--context-file"),"CloudSync",Arg("--receipt"));
            await receipt.RunAsync("HIST_C_OPEN_REPLICA",async test=>
                test.Check("open_view_convergence",await VerifyOpenReplicaViewsAsync(Arg("--data-dir"),test)));
            return receipt.Complete();
        }
        // Component proof through real WPF viewmodels and SQLite publication.
        // This does not stand in for the authenticated server/two-POS E2E.
        private static async Task<bool> VerifyOpenReplicaViewsAsync(string data,HistoricalSalesQaCase test)
        {
            using var f=await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data);
            var admitted=await ApplySyntheticPublicationAsync(f);var original=admitted.Receipt;var service=f.CreateWorkflow();
            using var report=new DailyReportViewModel(service,permissions:new PermissionService(f.Operator),session:f.Operator);
            using var import=new HistoricalSalesImportViewModel(service,f.Operator);
            report.ReportOriginIndex=2;report.SelectedTabIndex=1;report.HistoryFromText="2000-01-02";report.HistoryToText="2000-01-03";
            await report.ReloadAnalyticalAsync();await import.RefreshBatchesForTestingAsync();
            if(!report.ReportNumbersVisible||report.PeriodNetAmount!=1000||report.HistoryRows.Count!=2)return false;
            report.HistoryRows[0].IsMarked=false;report.SelectedHistoryRow=report.HistoryRows[1];
            import.SelectedBatch=import.Batches.Single();var before=f.State();
            var outbox=new HistoricalPublicationOutbox(f.Factory);
            var pending=await outbox.ReadSummaryAsync(f.ShopId,CancellationToken.None);
            var pendingText=import.PublicationStatus;
            var pendingSeen=pending.LocalOperations==1&&pending.Pending==1&&pending.Confirmed==0&&pending.Attention==0&&!string.IsNullOrEmpty(pendingText);
            await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(f.Generation,1);
            var attempt=await outbox.ClaimAsync(f.Generation,()=>{},1000,CancellationToken.None);
            var proof=admitted.Proof;
            const string serverReceiptRevision="9007199254740993";
            await outbox.ConfirmAsync(attempt,new HistoricalPublicationServerReceipt{OperationId=proof.OperationId,GrantId=proof.GrantId,ShopId=proof.ShopId,DeviceId=proof.DeviceId,
                DatasetId=proof.ServerDatasetId,LocalDatasetId=proof.DatasetId,RestoreEpoch=proof.RestoreEpoch.ToString(CultureInfo.InvariantCulture),RestoreLineageId=proof.RestoreLineageId,
                WriterEpoch=proof.WriterEpoch.ToString(CultureInfo.InvariantCulture),Revision=serverReceiptRevision,ContentDigest=proof.ContentDigest,LocalReceiptSha256=Hash(attempt.ReceiptJson),
                BatchId=proof.CanonicalDecision.BatchId,Kind="Apply",Inserted=original.InsertedCount,Duplicates=original.DuplicateCount,SignedGrossClp=original.SignedAmountClp.ToString(CultureInfo.InvariantCulture)},f.Generation,()=>{},1001,CancellationToken.None);
            var deliveryClock=Stopwatch.StartNew();
            while(deliveryClock.ElapsedMilliseconds<3000&&(import.IsBusy||!import.PublicationStatus.Contains(serverReceiptRevision)))await Task.Delay(10);
            var confirmedSeen=!import.IsBusy&&import.PublicationStatus!=pendingText&&import.PublicationStatus.Contains(serverReceiptRevision)&&import.SelectedBatch?.ApplyOperationId==original.OperationId&&before==f.State();
            var deliveryMilliseconds=deliveryClock.ElapsedMilliseconds;
            const string dataset="90000000-0000-4000-8000-000000003019",batch="80000000-0000-4000-8000-000000003019",merchant="70000000-0000-4000-8000-000000003019",cursor="60000000-0000-4000-8000-000000003019",movement="40000000-0000-4000-8000-000000003019";
            var ticks=new DateTime(2000,1,2,13,0,0).Ticks;
            string Hash(string value){using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant();}
            var value=new HistoricalReplicaValue{Id=movement,MerchantId=merchant,BatchId=batch,
                IdentityKey=Hash(f.ShopId+"\u001fgetnet\u001f"+merchant+"\u001fgetnet.legacy.transaction\u001fremote-only"),
                Active=true,BusinessTicks=ticks.ToString(CultureInfo.InvariantCulture),GrossClp="5000",FeeClp="0",FeeVatClp=null,NetClp="5000",
                MovementKind="Sale",TransactionState="ABONADO",ExternalNamespace="getnet.legacy.transaction",ExternalId="remote-only",
                SourceSheet="synthetic",SourceRow="2",TimestampRaw="02/01/2000 13:00:00",TimeInterpretation="Unresolved",
                TimePolicyVersion="synthetic-v1",TimeEvidenceReference="synthetic-component",CardSubtype="Debit",CreatedRevision="1"};
            value.EconomicDigest=new HistoricalSalesEconomicValue(ticks,5000,0,null,5000,"Sale","ABONADO").Fingerprint;
            var entities=new[]{new HistoricalReplicaEntity{Kind="batch",Id=batch,Payload=new HistoricalReplicaValue{Id=batch,Origin="web_import",State="active",ApplyOperationId=cursor,CreatedRevision="1",CreatedUtcMs="1"}},
                new HistoricalReplicaEntity{Kind="movement",Id=movement,Payload=value}};
            foreach(var entity in entities)entity.Digest=HistoricalReplicaProtocol.EntityDigest(entity);
            var checksum=HistoricalReplicaProtocol.SnapshotChecksum(f.ShopId,dataset,1,2,entities);
            var store=new HistoricalReplicaStore(f.Factory,()=>{});
            await store.ObserveAuthorityAsync(f.ShopId,new HistoricalCloudResult{DatasetId=dataset,Revision="1",WriterEpoch="2",WriterAuthority="web",ProtocolVersion=1},CancellationToken.None);
            // Let the open view observe the temporary coverage gap. Its user's
            // selection must survive the subsequent complete publication too.
            await Task.Delay(80);
            var manifest=new HistoricalCloudResult{DatasetId=dataset,Revision="1",WriterEpoch="2",Cursor=cursor,Mode="snapshot",Checksum=checksum,Movements=1,Batches=1};
            var generation=await store.BeginAsync(f.ShopId,manifest,CancellationToken.None);
            await store.StageAsync(generation,new HistoricalCloudResult{DatasetId=dataset,Revision="1",WriterEpoch="2",Cursor=cursor,Done=true,Checksum=checksum,Movements=1,Batches=1,Entities=entities},CancellationToken.None);
            var clock=Stopwatch.StartNew();await store.PublishAsync(generation,CancellationToken.None);
            while(clock.ElapsedMilliseconds<3000&&(!report.ReportNumbersVisible||report.PeriodNetAmount!=5000||report.IsBusy||import.IsBusy||import.CanImport))
                await Task.Delay(10);
            var converged=report.ReportNumbersVisible&&report.PeriodNetAmount==5000&&!report.IsBusy&&!import.CanImport;
            var selection=report.SelectedTabIndex==1&&report.HistoryFromText=="2000-01-02"&&report.HistoryToText=="2000-01-03"&&
                report.SelectedHistoryRow?.Date==new DateTime(2000,1,3)&&!report.HistoryRows[0].IsMarked&&report.HistoryRows[1].IsMarked&&
                import.SelectedBatch?.ApplyOperationId==original.OperationId;
            var replicaMilliseconds=clock.ElapsedMilliseconds;
            var previousReception=import.ReceptionStatus;var observationClock=Stopwatch.StartNew();
            await store.ObserveAuthorityAsync(f.ShopId,new HistoricalCloudResult{DatasetId=dataset,Revision="2",WriterEpoch="2",WriterAuthority="web",ProtocolVersion=1},CancellationToken.None);
            while(observationClock.ElapsedMilliseconds<3000&&(import.IsBusy||import.ReceptionStatus==previousReception))await Task.Delay(10);
            var newerKnown=!import.IsBusy&&import.ReceptionStatus!=previousReception&&!string.IsNullOrEmpty(import.ReceptionStatus)&&report.ReportNumbersVisible&&report.PeriodNetAmount==5000&&before==f.State();
            test.PrivateDiagnostic("open_replica_views;converged="+converged+";selection="+selection+";pending_seen="+pendingSeen+";confirmed_seen="+confirmedSeen+";newer_known="+newerKnown+";delivery_ms="+deliveryMilliseconds+";unchanged="+(before==f.State())+";elapsed_ms="+replicaMilliseconds+";observation_ms="+observationClock.ElapsedMilliseconds);
            return converged&&selection&&pendingSeen&&confirmedSeen&&newerKnown&&before==f.State();
        }
        private static async Task<(HistoricalSalesOperationReceipt Receipt,HistoricalPublicationProof Proof)> ApplySyntheticPublicationAsync(HistoricalSalesFoundationRunner.Fixture f)
        {
            var plan=await f.PlanAsync();var state=await f.Service.ReadDatasetAsync(CancellationToken.None);
            using var draft=HistoricalPublicationDraft.ForApply(plan,f.Generation.ShopDeviceId,state.RestoreLineageId);
            // Simulated server DTO for a component test, not a real permit or API proof.
            var fields=new Dictionary<string,string>{{"grant_id",Guid.NewGuid().ToString("D")},{"operation_id",Guid.Parse(plan.OperationId).ToString("D")},
                {"shop_id",f.ShopId},{"device_id",f.Generation.ShopDeviceId},{"local_dataset_id",plan.DatasetId},{"server_dataset_id","90000000-0000-4000-8000-000000003019"},
                {"restore_lineage_id",draft.RestoreLineageId},{"restore_epoch",plan.RestoreEpoch.ToString(CultureInfo.InvariantCulture)},{"writer_epoch","1"},
                {"content_digest",draft.ContentDigest},{"request_fingerprint",plan.RequestFingerprint},{"decision","content_authorized_v2"}};
            var preview=await f.Service.PreviewAsync(plan,CancellationToken.None);
            var proof=HistoricalPublicationAdmissionFixture.MakeProof(fields,new HistoricalPublicationAdmissionFixture.Decision{Inserted=preview.NewCount,
                Duplicates=preview.DuplicateCount,Rejected=plan.Analysis.RejectedRows.Count,Corrections=plan.CorrectionApprovals.Count,Gross=preview.NewGrossClp},"9007199254740993");
            var journal=new HistoricalPublicationJournal(f.Factory);
            var nonce=await journal.SaveIntentAsync(draft,proof.ServerDatasetId,proof.WriterEpoch,()=>f.Factory.AccessContext.DemandPath(f.Factory.DbPath),CancellationToken.None);
            await journal.SaveGrantAsync(draft.OperationId,nonce,proof,()=>f.Factory.AccessContext.DemandPath(f.Factory.DbPath),CancellationToken.None);
            using var use=await f.Operator.BeginHistoricalAuthorizationUseAsync(HistoricalImportOperationKind.Apply,plan.OperationId,plan.RequestFingerprint,plan.DatasetId,plan.RestoreEpoch,
                ()=>f.Factory.AccessContext.DemandPath(f.Factory.DbPath),CancellationToken.None);
            HistoricalPublicationAdmissionFixture.AttachLiveGrant(use.Guard,proof,()=>f.Factory.AccessContext.DemandPath(f.Factory.DbPath));
            var receipt=await f.Service.ApplyAsync(plan,use.Guard,f.Backup(),CancellationToken.None,publicationDraft:draft,publicationProof:proof);
            return(receipt,proof);
        }
    }
}
