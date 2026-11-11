using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Persistence;

// Test-only console entry: build the existing test assembly with OutputType=Exe
// and StartupObject set to this class. Ordinary MSTest discovery adds no skip.
// Uses the real Core/Data client, supervisor, HTTP, export, ledger and replica.
// Local actor composition is synthetic; this does not attest WPF/Win7 hardware.
internal static class AuthenticatedHistoryServiceHarness
{
    sealed class Actor
    {
        public string deviceId{get;set;}="";public string sessionId{get;set;}="";
        public string deviceToken{get;set;}="";public string sessionToken{get;set;}="";
    }
    sealed class Scope
    {
        public string schema{get;set;}="";public string nonce{get;set;}="";public string endpoint{get;set;}="";
        public string clientSha{get;set;}="";public string shopId{get;set;}="";public string staffId{get;set;}="";
        public int credentialVersion{get;set;}public Actor actorA{get;set;}=new();public Actor actorB{get;set;}=new();
        public bool extendedCases{get;set;}
    }
    sealed class Fact
    {
        public string Namespace{get;set;}="";public string Id{get;set;}="";public string Ticks{get;set;}="";public string Gross{get;set;}="";
        public string? Fee{get;set;}public string? Vat{get;set;}public string? Net{get;set;}
        internal string BasicKey=>Namespace+"|"+Id+"|"+Ticks+"|"+Gross;
        internal string Key=>BasicKey+"|"+(Fee??"null")+"|"+(Vat??"null")+"|"+(Net??"null");
    }
    sealed class Binding{public string Scope{get;set;}="";public string Id{get;set;}="";public string Evidence{get;set;}="";}
    static string _phase="preflight",_directory="",_lastCode="";static Scope _scope=new();
    static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=false};
    static void Demand(bool condition,string code){if(!condition)throw new InvalidOperationException(code);}
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Demand(args.Length==1&&Path.IsPathFullyQualified(args[0]),"history_test_context_required");
            _directory=Path.GetDirectoryName(Path.GetFullPath(args[0]))!;
            Win7POS.Data.PosDatabasePathIdentity.RejectReparseComponents(args[0]);
            _scope=JsonSerializer.Deserialize<Scope>(await File.ReadAllTextAsync(args[0]),Json)??throw new InvalidDataException();
            Demand(_scope.schema=="asus019-authenticated-history-v2"&&Guid.TryParseExact(_scope.nonce,"N",out _)&&_scope.shopId==HistoricalCloudExportTests.Fixture.Shop,"history_test_scope_invalid");
            var endpoint=new Uri(_scope.endpoint);Demand(endpoint.IsLoopback&&endpoint.Scheme=="http"&&endpoint.UserInfo==""&&endpoint.Query==""&&endpoint.Fragment==""&&endpoint.AbsolutePath=="/","history_test_loopback_required");
            HistoricalReplicaProtocol.DemandId(_scope.staffId);HistoricalReplicaProtocol.DemandId(_scope.actorA.deviceId);HistoricalReplicaProtocol.DemandId(_scope.actorB.deviceId);
            Demand(_scope.actorA.deviceId!=_scope.actorB.deviceId&&_scope.credentialVersion>0&&System.Text.RegularExpressions.Regex.IsMatch(_scope.clientSha,"^[0-9a-f]{40}$"),"history_test_scope_invalid");
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(_scope.extendedCases?6:4));var ct=deadline.Token;
            await using var a=await Session.Create(_scope.actorA,true);await using var b=await Session.Create(_scope.actorB,false);
            var original=a.LocalFacts();Demand(original.Length>0&&b.LocalFacts().Length==0,"history_test_initial_ledger");
            _phase="initial_baseline";await a.Until(a.HasBaseline,ct);await b.Until(b.HasBaseline,ct);await a.Turn(ct);
            var before=a.Facts();Same(original,before);Same(before,b.Facts());a.NoNative();b.NoNative();
            var source=NewSource(a.Fixture);var analysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{source},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
            var state=await a.Fixture.Service.ReadDatasetAsync(ct);
            var assignments=ReadAssignments(a,analysis);
            var plan=new HistoricalSalesImportPlan(Guid.NewGuid().ToString("N"),_scope.shopId,state.DatasetId,state.DataRevision,0,analysis,assignments,new DateTime(2025,1,1),"Synthetic service harness approved start");
            var expected=analysis.AcceptedTransactions.Where(t=>t.ExternalId.StartsWith("8",StringComparison.Ordinal)&&t.ExternalId.Length==19).Select(ToFact).ToArray();
            Demand(expected.Length>0,"history_test_new_source_missing");
            _phase="source_ready";await SaveState(new{phase=_phase,nonce=_scope.nonce,baselineCount=before.Length,sourceSha=Hash(await File.ReadAllBytesAsync(source)),expected},ct);
            await WaitSignal("allow_local_apply",ct);
            _phase="local_apply";var authority=await a.Request("authority",new(),ct);
            using var draft=await a.Fixture.Service.PreparePublicationDraftAsync(plan,a.Generation.ShopDeviceId,ct);
            var admission=await new HistoricalPublicationPreparer(a.Fixture.Db.Factory,a.Demand,a.Request).PrepareAsync(draft,authority,ct);
            var live=new HistoricalLocalWriteGrant(Guid.Parse(plan.OperationId).ToString("D"),authority.WriterEpoch,admission.RemainingMilliseconds,
                admission.GrantRequestElapsedMilliseconds,a.Demand,clock:admission.ReadMonotonicMilliseconds,frequency:1000,publicationProof:admission.Proof,requestStartedTimestamp:admission.MonotonicRequestStartedMilliseconds);
            var guard=new HistoricalImportAuthorizationCommitGuard(HistoricalImportOperationKind.Apply,plan.OperationId,plan.RequestFingerprint,_scope.shopId,"SYNTHETIC",
                a.Generation.ShopDeviceId,1,plan.DatasetId,0,1,a.Generation.Fingerprint,a.Generation.StaffId,a.Generation.StaffCredentialVersion,a.Demand,(_,action)=>{a.Demand();action();a.Demand();});
            guard.AttachPublicationGrant(live);
            var receipt=await a.Fixture.Service.ApplyAsync(plan,guard,Path.Combine(a.Fixture.Output,"new-apply.db"),ct,publicationDraft:draft,publicationProof:admission.Proof);
            Demand(receipt.InsertedCount==expected.Length,"history_test_local_count_mismatch");
            _phase="published";
            await a.Until(async()=>{var status=await new HistoricalPublicationOutbox(a.Fixture.Db.Factory).ReadStatusAsync(plan.OperationId,_scope.shopId,ct);return status?.Status=="synchronized";},ct);
            var wanted=before.Concat(expected).OrderBy(x=>x.Key,StringComparer.Ordinal).ToArray();
            await b.Until(()=>Task.FromResult(Equal(wanted,b.Facts())),ct);await a.Until(()=>Task.FromResult(Equal(wanted,a.Facts())),ct);
            a.NoNative();b.NoNative();Same(a.Facts(),b.Facts());
            await SaveState(new{phase=_phase,nonce=_scope.nonce,status="PASS_CORE_DATA_ROUNDTRIP",count=wanted.Length,facts=wanted,factsSha=Hash(Encoding.UTF8.GetBytes(string.Join("\n",wanted.Select(x=>x.Key)))),newIds=expected.Select(x=>x.Id)},ct);
            await WaitSignal("admin_observed",ct);
            if(_scope.extendedCases)await DuplicateAndUndo(a,b,source,receipt,before,wanted,ct);
            _phase="complete";Console.WriteLine("authenticated_history_core_data_pass");return 0;
        }
        catch(Exception e)
        {
            if(_directory!="")await SaveState(new{phase=_phase,nonce=_scope.nonce,status="FAIL",exception=e.GetType().Name,code=ClosedCode(e.Message),lastCode=ClosedCode(_lastCode)},default);
            Console.Error.WriteLine("authenticated_history_client_failed");return 1;
        }
    }
    static string ClosedCode(string? value)=>value!=null&&System.Text.RegularExpressions.Regex.IsMatch(value,"^(?:history|historical)_[a-z_]+$")?value:"history_test_failed";
    static HistoricalMerchantAssignment[] ReadAssignments(Session session,HistoricalSalesImportAnalysis analysis)
    {
        // Release this reader before publication and exclusive Apply maintenance.
        using var c=session.Fixture.Db.Factory.Open();
        return analysis.AcceptedTransactions.Select(t=>t.Merchant).GroupBy(HistoricalMerchantAssignment.MerchantKey).Select(group=>{
            var binding=c.QuerySingle<Binding>("SELECT scope_id Scope,id Id,evidence_reference Evidence FROM historical_merchant_binding WHERE shop_id=@shop AND alias_key=@alias",new{shop=_scope.shopId,alias=group.Key});
            return new HistoricalMerchantAssignment(group.First(),binding.Scope,binding.Id,binding.Evidence);
        }).ToArray();
    }
    static async Task DuplicateAndUndo(Session a,Session b,string source,HistoricalSalesOperationReceipt first,Fact[] baseline,Fact[] published,CancellationToken ct)
    {
        _phase="duplicate";
        var analysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{source},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
        var state=await a.Fixture.Service.ReadDatasetAsync(ct);
        var plan=new HistoricalSalesImportPlan(Guid.NewGuid().ToString("N"),_scope.shopId,state.DatasetId,state.DataRevision,0,analysis,ReadAssignments(a,analysis),new DateTime(2025,1,1),"Synthetic service harness approved start");
        using(var draft=await a.Fixture.Service.PreparePublicationDraftAsync(plan,a.Generation.ShopDeviceId,ct))
        {
            var admission=await Admit(a,draft,ct);
            var duplicate=await a.Fixture.Service.ApplyAsync(plan,admission.guard,Path.Combine(a.Fixture.Output,"duplicate-apply.db"),ct,publicationDraft:draft,publicationProof:admission.admission.Proof);
            Demand(duplicate.InsertedCount==0&&duplicate.DuplicateCount==analysis.AcceptedTransactions.Count,"history_test_duplicate_effect");
        }
        await Converge(a,b,plan.OperationId,published,ct);await ObserveCase("duplicate",published,ct);
        _phase="undo";state=await a.Fixture.Service.ReadDatasetAsync(ct);var operation=Guid.NewGuid().ToString("N");
        using(var draft=await a.Fixture.Service.PrepareUndoPublicationDraftAsync(operation,_scope.shopId,a.Generation.ShopDeviceId,state.DatasetId,0,first.BatchId,1,ct))
        {
            var admission=await Admit(a,draft,ct);
            await a.Fixture.Service.UndoAsync(first.BatchId,1,admission.guard,Path.Combine(a.Fixture.Output,"undo-first.db"),ct,publicationDraft:draft,publicationProof:admission.admission.Proof);
        }
        await Converge(a,b,operation,baseline,ct);await ObserveCase("undo",baseline,ct);
        _phase="correction_source_ready";var corrected=CorrectionSource(a.Fixture,source);
        var correctionAnalysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{corrected},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
        var expected=correctionAnalysis.AcceptedTransactions.Where(t=>t.ExternalId.StartsWith("8",StringComparison.Ordinal)&&t.ExternalId.Length==19).Select(ToFact).ToArray();
        Demand(expected.Length==1&&expected[0].Key!=published.Single(x=>x.Id==expected[0].Id).Key,"history_test_correction_source");
        await SaveState(new{phase=_phase,nonce=_scope.nonce,sourceSha=Hash(await File.ReadAllBytesAsync(corrected)),expected},ct);await WaitSignal("allow_correction_apply",ct);
        _phase="correction";state=await a.Fixture.Service.ReadDatasetAsync(ct);operation=Guid.NewGuid().ToString("N");
        var assignments=ReadAssignments(a,correctionAnalysis);
        var tentative=new HistoricalSalesImportPlan(operation,_scope.shopId,state.DatasetId,state.DataRevision,0,correctionAnalysis,assignments,new DateTime(2025,1,1),"Synthetic service harness approved start");
        var preview=await a.Fixture.Service.PreviewAsync(tentative,ct);
        Demand(preview.ConflictCount==0&&preview.CorrectionCount==1&&preview.Corrections.Count==1&&!preview.CorrectionsTruncated,"history_test_correction_preview");
        var correction=new HistoricalSalesImportPlan(operation,_scope.shopId,state.DatasetId,state.DataRevision,0,correctionAnalysis,assignments,new DateTime(2025,1,1),"Synthetic service harness approved start",new[]{preview.Corrections[0].Approve("Synthetic correction after verified Undo")});
        using(var draft=await a.Fixture.Service.PreparePublicationDraftAsync(correction,a.Generation.ShopDeviceId,ct))
        {
            var admission=await Admit(a,draft,ct);
            var applied=await a.Fixture.Service.ApplyAsync(correction,admission.guard,Path.Combine(a.Fixture.Output,"correction-apply.db"),ct,publicationDraft:draft,publicationProof:admission.admission.Proof);
            Demand(applied.InsertedCount==1&&applied.CorrectionCount==1,"history_test_correction_count");
        }
        var wanted=baseline.Concat(expected).ToArray();await Converge(a,b,operation,wanted,ct);await ObserveCase("correction",wanted,ct);
    }
    static Fact ToFact(HistoricalSalesTransaction t)=>new(){Namespace=t.ExternalNamespace,Id=t.ExternalId,Ticks=t.BusinessDateTime!.Value.Ticks.ToString(CultureInfo.InvariantCulture),Gross=t.GrossClp.ToString(CultureInfo.InvariantCulture),Fee=t.CommissionClp?.ToString(CultureInfo.InvariantCulture),Vat=t.CommissionVatClp?.ToString(CultureInfo.InvariantCulture),Net=t.NetClp?.ToString(CultureInfo.InvariantCulture)};
    static string CorrectionSource(HistoricalCloudExportTests.Fixture fixture,string source)
    {
        var analysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{source},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
        var row=analysis.AcceptedTransactions.Single(t=>t.ExternalId.StartsWith("8",StringComparison.Ordinal)&&t.ExternalId.Length==19);
        Demand(row.GrossClp>0,"history_test_correction_sale_required");
        var target=Path.Combine(fixture.Output,"corrected-legacy.xlsx");using var input=ZipFile.OpenRead(source);using var output=ZipFile.Open(target,ZipArchiveMode.Create);var changed=false;
        foreach(var entry in input.Entries)
        {
            var next=output.CreateEntry(entry.FullName,CompressionLevel.Optimal);using var read=entry.Open();using var write=next.Open();
            if(entry.FullName=="xl/worksheets/sheet1.xml")
            {
                var xml=XDocument.Load(read);XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var rows=xml.Descendants(ns+"row");var header=rows.Single(r=>(string?)r.Attribute("r")=="1");
                Demand(header.Elements(ns+"c").Single(c=>(string?)c.Attribute("r")=="C1").Value.StartsWith("VALOR",StringComparison.Ordinal)&&header.Elements(ns+"c").Single(c=>(string?)c.Attribute("r")=="G1").Value.StartsWith("MONTO",StringComparison.Ordinal),"history_test_correction_fixture_layout");
                var data=rows.Single(r=>(string?)r.Attribute("r")==row.Source.RowNumber.ToString(CultureInfo.InvariantCulture));
                void Set(string column,long value){var cell=data.Elements(ns+"c").Single(c=>(string?)c.Attribute("r")==column+row.Source.RowNumber.ToString(CultureInfo.InvariantCulture));cell.SetAttributeValue("t","n");cell.RemoveNodes();cell.Add(new XElement(ns+"v",value.ToString(CultureInfo.InvariantCulture)));}
                Set("C",checked(row.GrossClp+1));if(row.NetClp.HasValue)Set("G",checked(row.NetClp.Value+1));changed=true;xml.Save(write);
            }else read.CopyTo(write);
        }
        Demand(changed,"history_test_correction_sheet_missing");return target;
    }
    static async Task<(HistoricalImportAuthorizationCommitGuard guard,HistoricalPublicationAdmission admission)> Admit(Session session,HistoricalPublicationDraft draft,CancellationToken ct)
    {
        var authority=await session.Request("authority",new(),ct);
        var admission=await new HistoricalPublicationPreparer(session.Fixture.Db.Factory,session.Demand,session.Request).PrepareAsync(draft,authority,ct);
        var live=new HistoricalLocalWriteGrant(Guid.Parse(draft.OperationId).ToString("D"),authority.WriterEpoch,admission.RemainingMilliseconds,
            admission.GrantRequestElapsedMilliseconds,session.Demand,clock:admission.ReadMonotonicMilliseconds,frequency:1000,publicationProof:admission.Proof,requestStartedTimestamp:admission.MonotonicRequestStartedMilliseconds);
        var guard=new HistoricalImportAuthorizationCommitGuard(Enum.Parse<HistoricalImportOperationKind>(draft.Kind),draft.OperationId,draft.RequestFingerprint,draft.ShopId,"SYNTHETIC",
            session.Generation.ShopDeviceId,1,draft.DatasetId,draft.RestoreEpoch,1,session.Generation.Fingerprint,session.Generation.StaffId,session.Generation.StaffCredentialVersion,session.Demand,(_,action)=>{session.Demand();action();session.Demand();});
        guard.AttachPublicationGrant(live);return(guard,admission);
    }
    static async Task Converge(Session a,Session b,string operation,Fact[] wanted,CancellationToken ct)
    {
        await a.Until(async()=>{var status=await new HistoricalPublicationOutbox(a.Fixture.Db.Factory).ReadStatusAsync(operation,_scope.shopId,ct);return status?.Status=="synchronized";},ct);
        await b.Until(()=>Task.FromResult(Equal(wanted,b.Facts())),ct);await a.Until(()=>Task.FromResult(Equal(wanted,a.Facts())),ct);
        Same(a.Facts(),b.Facts());a.NoNative();b.NoNative();
    }
    static async Task ObserveCase(string phase,Fact[] facts,CancellationToken ct)
    {
        await SaveState(new{phase,nonce=_scope.nonce,status="PASS_CORE_DATA_CASE",count=facts.Length,factsSha=Hash(Encoding.UTF8.GetBytes(string.Join("\n",facts.Select(x=>x.Key).OrderBy(x=>x,StringComparer.Ordinal))))},ct);
        await WaitSignal(phase+"_observed",ct);
    }
    static async Task SaveState(object value,CancellationToken ct)
    {
        var target=Path.Combine(_directory,"client-state.private.json");var temporary=target+"."+Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(value),ct);File.Move(temporary,target,true);
    }
    static async Task WaitSignal(string phase,CancellationToken ct)
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();var file=Path.Combine(_directory,"client-control.private.json");
        while(clock.Elapsed<TimeSpan.FromSeconds(60))
        {
            ct.ThrowIfCancellationRequested();if(File.Exists(file)){using var json=JsonDocument.Parse(await File.ReadAllTextAsync(file,ct));if(json.RootElement.GetProperty("nonce").GetString()==_scope.nonce&&json.RootElement.GetProperty("phase").GetString()==phase)return;}
            await Task.Delay(100,ct);
        }
        throw new InvalidOperationException("history_test_browser_signal_timeout");
    }
    static string NewSource(HistoricalCloudExportTests.Fixture f)
    {
        var original=Path.Combine(AppContext.BaseDirectory,"fixtures","historical-sales-getnet-v1","synthetic-legacy.xlsx");
        var analyzed=new HistoricalSalesImportAnalyzer().Analyze(new[]{original},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
        var old=analyzed.AcceptedTransactions[0].ExternalId;var id="8"+DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        var target=Path.Combine(f.Output,"new-legacy.xlsx");using var input=ZipFile.OpenRead(original);using var output=ZipFile.Open(target,ZipArchiveMode.Create);var changed=0;
        foreach(var entry in input.Entries)
        {
            var next=output.CreateEntry(entry.FullName,CompressionLevel.Optimal);using var read=entry.Open();using var write=next.Open();
            if(entry.FullName.StartsWith("xl/",StringComparison.Ordinal)&&entry.FullName.EndsWith(".xml",StringComparison.Ordinal))
            {
                var xml=XDocument.Load(read);foreach(var value in xml.Descendants().Where(x=>x.Name.LocalName=="t"||x.Name.LocalName=="v"))if(value.Value==old){value.Value=id;changed++;}xml.Save(write);
            }else read.CopyTo(write);
        }
        Demand(changed>0,"history_test_source_mutation_missing");return target;
    }
    static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    static bool Equal(Fact[] a,Fact[] b)=>a.Select(x=>x.Key).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(b.Select(x=>x.Key).OrderBy(x=>x,StringComparer.Ordinal));
    static void Same(Fact[] a,Fact[] b)=>Demand(Equal(a,b),"history_test_replica_mismatch");
    sealed class Session:IDisposable,IAsyncDisposable
    {
        internal HistoricalCloudExportTests.Fixture Fixture=null!;internal OnlineSyncGeneration Generation=null!;
        PosAdminWebClient _client=null!;OnlineSyncSupervisor _supervisor=null!;Actor _actor=new();bool _active=true;string _server="";long _epoch;
        internal static async Task<Session> Create(Actor actor,bool legacy)
        {
            var s=new Session{_actor=actor};s.Fixture=await HistoricalCloudExportTests.Fixture.CreateAsync(deviceId:actor.deviceId,seedLegacy:legacy);s.Fixture.KeepFiles=true;
            s.Generation=new OnlineSyncGeneration(Guid.NewGuid().ToString("D"),actor.sessionId,actor.deviceId,_scope.shopId,"synthetic-service-harness",_scope.staffId,_scope.credentialVersion);
            await new OnlineSyncGenerationRepository(s.Fixture.Db.Factory).ActivateAndRecoverAsync(s.Generation,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var options=new PosAdminWebOptions(new Uri(_scope.endpoint));s._client=new PosAdminWebClient(options);
            var root=Path.Combine(Path.GetTempPath(),"s-"+Guid.NewGuid().ToString("N"));
            var sync=new PosHistoricalCloudSyncService(s.Fixture.Db.Factory,async(context,state,ct)=>{
                s.Demand();AuthenticatedHistoryServiceHarness.Demand(context.State=="missing"&&context.CanPrepare==true,"history_test_prepare_denied");
                var guard=new HistoricalCloudExportGuard(_scope.shopId,actor.deviceId,state.DatasetId,state.DataRevision,0,s.Demand);
                var receipt=await new HistoricalBaselineSource(s.Fixture.Db.Factory,root).PrepareAsync(guard,Guid.Parse(state.RestoreLineageId).ToString("D"),ct);
                return new HistoricalBaselinePreparation(receipt,guard);
            });
            s._supervisor=new OnlineSyncSupervisor(s.Generation,(context,trigger,ct)=>sync.SynchronizeAsync(options,context,_scope.clientSha,()=>s._active,ct),
                generation=>new OnlineSyncGenerationRepository(s.Fixture.Db.Factory).IsCurrentAndActiveAsync(generation),
                (_,_)=>{s._active=false;return Task.CompletedTask;},credentialProvider:_=>Task.FromResult(new OnlineSyncRequestCredentials(s.Generation,actor.deviceToken,actor.sessionToken,"synthetic-service-harness")));
            var authority=await s.Request("authority",new(),default);s._server=authority.DatasetId;s._epoch=HistoricalReplicaProtocol.Integer(authority.WriterEpoch);return s;
        }
        internal void Demand()=>AuthenticatedHistoryServiceHarness.Demand(_active,"history_test_generation_stopped");
        internal async Task<HistoricalCloudResult> Request(string operation,HistoricalCloudPayload payload,CancellationToken ct)
        {
            Demand();var result=await _client.HistoricalCloudAsync(new HistoricalCloudRequest{DeviceId=_actor.deviceId,SessionId=_actor.sessionId,DeviceToken=_actor.deviceToken,SessionToken=_actor.sessionToken,Operation=operation,Payload=payload},ct);
            _lastCode=result.Code;
            AuthenticatedHistoryServiceHarness.Demand(result.Success&&result.Value?.Ok==true&&result.Value.ShopId==_scope.shopId&&result.Value.DeviceId==_actor.deviceId&&result.Value.SessionId==_actor.sessionId,"history_test_http_request_rejected");return result.Value!.Result;
        }
        internal async Task<bool> HasBaseline()
        {
            var f=await new HistoricalBaselineStore(Fixture.Db.Factory,Generation,Demand).ReadAsync(_server,_epoch,default);if(f?.State!="applied")return false;
            using var c=Fixture.Db.Factory.Open();return c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_read_mode WHERE flow_id=@FlowId",f)==1;
        }
        internal async Task Turn(CancellationToken ct){var result=await _supervisor.TriggerAsync(OnlineSyncLane.HistoricalPayments,OnlineSyncLaneTrigger.Manual,ct);_lastCode=result.Code;AuthenticatedHistoryServiceHarness.Demand(result.Success,"history_test_supervisor_turn_failed");}
        internal async Task Until(Func<Task<bool>> ready,CancellationToken ct){for(var i=0;i<80;i++){await Turn(ct);if(await ready())return;await Task.Delay(500,ct);}throw new InvalidOperationException("history_test_convergence_timeout");}
        internal Fact[] Facts()
        {
            using var c=Fixture.Db.Factory.Open();
            var facts=c.Query<Fact>("SELECT e.external_namespace Namespace,e.external_id Id,CAST(e.business_ticks AS TEXT) Ticks,CAST(e.signed_amount_clp AS TEXT) Gross,CAST(e.signed_fee_clp AS TEXT) Fee,CAST(e.signed_fee_vat_clp AS TEXT) Vat,CAST(e.signed_net_clp AS TEXT) Net FROM historical_cloud_entity e JOIN historical_cloud_state s ON s.shop_id=e.shop_id AND s.generation_id=e.generation_id WHERE e.shop_id=@shop AND e.entity_kind='movement' AND e.is_active=1",new{shop=_scope.shopId}).ToArray();
            var visible=c.Query<Fact>("SELECT external_namespace Namespace,external_id Id,CAST(business_ticks AS TEXT) Ticks,CAST(signed_amount_clp AS TEXT) Gross FROM historical_baseline_report_movements WHERE shop_id=@shop AND is_active=1",new{shop=_scope.shopId}).ToArray();
            AuthenticatedHistoryServiceHarness.Demand(facts.Select(f=>f.BasicKey).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(visible.Select(f=>f.BasicKey).OrderBy(x=>x,StringComparer.Ordinal)),"history_test_report_replica_mismatch");
            return facts;
        }
        internal Fact[] LocalFacts(){using var c=Fixture.Db.Factory.Open();return c.Query<Fact>("SELECT external_namespace Namespace,external_id Id,CAST(business_ticks AS TEXT) Ticks,CAST(signed_amount_clp AS TEXT) Gross,CAST(signed_fee_clp AS TEXT) Fee,CAST(signed_fee_vat_clp AS TEXT) Vat,CAST(signed_net_clp AS TEXT) Net FROM historical_transactions WHERE shop_id=@shop AND is_active=1",new{shop=_scope.shopId}).ToArray();}
        internal void NoNative()
        {
            using var c=Fixture.Db.Factory.Open();AuthenticatedHistoryServiceHarness.Demand(c.ExecuteScalar<long>("SELECT (SELECT COUNT(*) FROM sales)+(SELECT COUNT(*) FROM sales_sync_outbox)+(SELECT COUNT(*) FROM local_stock_movements)")==0,"history_test_native_effect");
            if(Fixture.OriginalReceipt!="")AuthenticatedHistoryServiceHarness.Demand(c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation WHERE receipt_json=@receipt",new{receipt=Fixture.OriginalReceipt})==1,"history_test_original_audit_changed");
        }
        public void Dispose(){_active=false;_supervisor?.Dispose();_client?.Dispose();Fixture?.Dispose();}
        public async ValueTask DisposeAsync()
        {
            _active=false;
            try{if(_supervisor!=null)await _supervisor.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));}
            finally{Dispose();}
        }
    }
}
