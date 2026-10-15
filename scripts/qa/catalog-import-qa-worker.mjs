import { readFileSync, writeFileSync, appendFileSync, mkdirSync } from 'node:fs';
import { resolve, dirname, join, relative, isAbsolute } from 'node:path';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { createContext, Script } from 'node:vm';
import { createServer } from 'node:http';
import { execFileSync } from 'node:child_process';
import assert from 'node:assert/strict';
import { QA_REPLAY_POLICY, acceptedPostReplay, assertAcceptedPart } from './catalog-import-replay-protocol.mjs';
const hash=bytes=>'sha256:'+createHash('sha256').update(bytes).digest('hex');
const arg=name=>{const at=process.argv.indexOf(name);return at<0?null:process.argv[at+1];};
const config=JSON.parse(readFileSync(arg('--config'),'utf8').replace(/^\uFEFF/,''));
assert.ok(config.dependencyRoot,'Configured QA dependencyRoot required');
const requireReal=createRequire(resolve(config.dependencyRoot,'package.json'));
const ts=requireReal('typescript'),pg=requireReal('pg');
for(const key of ['workerSha256','terminalSha256','protocolSha256']) assert.match(config.qaScriptBindings?.[key]??'',/^[a-f0-9]{64}$/);
assert.equal(hash(readFileSync(resolve(process.argv[1]))),'sha256:'+config.qaScriptBindings.workerSha256);
assert.equal(hash(readFileSync(new URL('./catalog-import-replay-protocol.mjs',import.meta.url))),'sha256:'+config.qaScriptBindings.protocolSha256);
assert.equal(config.schemaVersion,'maximum-uniform-worker-config-v2');
const EXPECTED_COMMIT=config.sourceCommit,EXPECTED_TREE=config.sourceTree;
assert.match(EXPECTED_COMMIT,/^[a-f0-9]{40}$/);assert.match(EXPECTED_TREE,/^[a-f0-9]{40}$/);
const source=resolve(config.sourceRoot),run=resolve(config.runRoot);
assert.equal(config.sourceCommit,EXPECTED_COMMIT);
assert.equal(execFileSync('git',['rev-parse','HEAD'],{cwd:source,encoding:'utf8'}).trim(),EXPECTED_COMMIT);
assert.equal(execFileSync('git',['rev-parse','HEAD^{tree}'],{cwd:source,encoding:'utf8'}).trim(),EXPECTED_TREE);
execFileSync('git',['diff','--exit-code','HEAD','--','src','supabase/migrations'],{cwd:source});
assert.match(config.nonce,/^[a-f0-9]{32}$/);
assert.match(config.database,/^max_uniform_[a-f0-9]+$/);
assert.equal(config.pgHost,'127.0.0.1');
assert.ok(config.pgPort>1024 && config.pgPort<65536);
assert.equal(config.httpHost,'127.0.0.1');
assert.ok(config.httpPort>1024 && config.httpPort<65536 && config.httpPort!==config.pgPort);
const bindingBytes=readFileSync(config.inputsBindingManifest);
assert.equal(hash(bindingBytes),'sha256:'+config.inputsBindingManifestSha256);
const binding=JSON.parse(bindingBytes.toString('utf8').replace(/^\uFEFF/,''));
assert.equal(binding.schemaVersion,'win7pos-maximum-inputs-binding-v2');
assert.equal(binding.adminSourceCommit,EXPECTED_COMMIT);assert.equal(binding.adminSourceTree,EXPECTED_TREE);
assert.equal(binding.frozenPosCommit,config.finalPosInputBinding.frozenPosCommit);
assert.equal(binding.inputsManifestSha256,config.finalPosInputBinding.inputsManifestSha256);
assert.equal(binding.fixtureManifestSha256,config.fixtureManifestSha256);
assert.equal(binding.planPartCount,config.finalPosInputBinding.partCount);
assert.equal(hash(readFileSync(config.fixtureManifest)),'sha256:'+config.fixtureManifestSha256);
mkdirSync(run,{recursive:true});
const loaded=new Map(),cache=new Map();
let sequence=1;
const phase=(name,fields={})=>appendFileSync(join(run,'phases.jsonl'),JSON.stringify({nonce:config.nonce,phase:name,startedAtUtc:new Date().toISOString(),sequence:sequence++,...fields})+'\n');
const rpcNames=new Set(['pos_runtime_lease_v1','pos_catalog_import_scope_v1','pos_catalog_import_recovery_v1','pos_runtime_audit_write_v1']);
let pool=null;
const signatures=new Map();
const adapter={rpc:async(name,args)=>{
  assert.ok(rpcNames.has(name),'Unapproved RPC '+name);
  assert.ok(pool,'Source inspection cannot invoke PostgreSQL');
  const client=await pool.connect();const begin=performance.now();
  try {
    if(!signatures.has(name)) {
      const result=await client.query(`select p.proargnames[1:p.pronargs] as names,
        array(select format_type(t,null) from unnest(p.proargtypes::oid[]) with ordinality a(t,i) order by i) as types
        from pg_proc p join pg_namespace n on n.oid=p.pronamespace where n.nspname='public' and p.proname=$1`,[name]);
      assert.equal(result.rows.length,1,'RPC overload/absence '+name);signatures.set(name,result.rows[0]);
    }
    const sig=signatures.get(name);assert.deepEqual(Object.keys(args).sort(),[...sig.names].sort());
    const values=sig.names.map((key,at)=>sig.types[at]==='jsonb'?JSON.stringify(args[key]):args[key]);
    const named=sig.names.map((key,at)=>`"${key}" => $${at+1}::${sig.types[at]}`).join(',');
    await client.query('begin');await client.query('set local role service_role');
    await client.query("set local request.jwt.claim.role='service_role'");
    const sqlStart=performance.now();
    const result=await client.query(`select public."${name}"(${named}) as result`,values);
    const sqlQueryRoundtripMs=performance.now()-sqlStart,commitStart=performance.now();
    await client.query('commit');
    const commitRoundtripMs=performance.now()-commitStart;
    appendFileSync(join(run,'rpc.jsonl'),JSON.stringify({name,action:args.p_action??null,durationMs:performance.now()-begin,sqlQueryRoundtripMs,commitRoundtripMs,ok:result.rows[0].result?.ok??null})+'\n');
    return {data:result.rows[0].result,error:null};
  } catch(error) {
    await client.query('rollback').catch(()=>{});
    appendFileSync(join(run,'rpc-errors.jsonl'),JSON.stringify({name,action:args.p_action??null,code:error.code??null,message:error.message,durationMs:performance.now()-begin})+'\n');
    return {data:null,error:{code:error.code??'fixture_adapter_failure',message:error.message}};
  } finally {client.release();}
}};
const overrides={'server-only':{},'@/lib/supabase/admin':{resolveSupabaseAdminConfig:()=>({status:'configured'}),createSupabaseAdminClient:()=>adapter}};
function module(path) {
  path=resolve(path);const rel=relative(source,path);assert.ok(rel && !rel.startsWith('..') && !isAbsolute(rel));
  if(cache.has(path))return cache.get(path).exports;
  const bytes=readFileSync(path),code=bytes.toString('utf8');
  const gitBytes=execFileSync('git',['show',`${EXPECTED_COMMIT}:${rel.replaceAll('\\','/')}`],{cwd:source,maxBuffer:16*1024*1024});
  assert.equal(hash(Buffer.from(code.replaceAll('\r\n','\n'))),hash(gitBytes),'Source drift '+rel);
  loaded.set(rel.replaceAll('\\','/'),{rawFileSha256:hash(bytes),gitContentSha256:hash(gitBytes),gitBlob:execFileSync('git',['rev-parse',`${EXPECTED_COMMIT}:${rel.replaceAll('\\','/')}`],{cwd:source,encoding:'utf8'}).trim()});
  const output=ts.transpileModule(code,{compilerOptions:{target:ts.ScriptTarget.ES2022,module:ts.ModuleKind.CommonJS}}).outputText;
  const m={exports:{}};cache.set(path,m);
  new Script(output,{filename:path}).runInContext(createContext({module:m,exports:m.exports,
    require:id=>{if(id in overrides)return overrides[id];if(id.startsWith('@/'))return module(resolve(source,'src',id.slice(2)+'.ts'));if(id.startsWith('.'))return module(resolve(dirname(path),id+'.ts'));return requireReal(id);},
    Date,Map,Set,Buffer,Request,Response,TextDecoder,TextEncoder,Uint8Array,URL,process,console,setTimeout,clearTimeout,structuredClone}));
  return m.exports;
}
const route=module(join(source,'src/app/api/pos/catalog/import-recovery/[action]/route.ts'));
writeFileSync(join(run,'worker-source-manifest.json'),JSON.stringify({sourceCommit:EXPECTED_COMMIT,sourceTree:EXPECTED_TREE,typescriptVersion:ts.version,files:Object.fromEntries(loaded),inputsBindingManifestSha256:config.inputsBindingManifestSha256,fixtureManifestSha256:config.fixtureManifestSha256,forwardMigration:binding.forwardMigration,adapter:'SupabaseAdmin connection factory only; all approved RPCs execute actual PostgreSQL SQL under service_role; route/parser/auth/tokens/runtime-boundary unchanged',workerRuntime:'Node.js '+process.version+' local HTTP route; Cloudflare isolate runtime NOT QUALIFIED'},null,2));
if(process.argv.includes('--inspect-source')) {console.log(JSON.stringify({inspected:true,files:loaded.size,sourceCommit:EXPECTED_COMMIT}));process.exit(0);}
assert.equal(config.hostWindowReleased,true,'Coordinator host window required before server start');
assert.ok(config.finalPosInputBinding,'Final frozen POS input binding required');
pool=new pg.Pool({host:config.pgHost,port:config.pgPort,database:config.database,user:config.pgUser,password:config.pgPassword,max:2,application_name:'maximum-uniform-'+config.nonce});
const identity=await pool.query('select current_database() as database, host(inet_server_addr()) as host, inet_server_port() as port, current_setting(\'data_directory\') as data_directory');
assert.equal(identity.rows[0].database,config.database);assert.equal(identity.rows[0].host,config.pgHost);assert.equal(identity.rows[0].port,config.pgPort);
assert.equal(resolve(identity.rows[0].data_directory),resolve(config.pgDataDirectory));
assert.equal(readFileSync(join(config.pgDataDirectory,'ownership-nonce.txt'),'utf8').trim(),config.nonce);
let requestIndex=0;const schedule=[],acceptedParts=new Set();
assert.equal(config.replayEachRequest,false,'Legacy all-request inband replay must be explicitly disabled');
assert.equal(config.qaReplayPolicy,QA_REPLAY_POLICY,'Explicit reviewed apply/other-action replay protocol required');
writeFileSync(join(run,'qa-replay-policy.json'),JSON.stringify({schemaVersion:'maximum-uniform-qa-replay-policy-v1',nonce:config.nonce,qaReplayPolicy:QA_REPLAY_POLICY,workerScriptSha256:hash(readFileSync(resolve(process.argv[1]))),applyInbandReplay:false,otherAcceptedPostActionsInbandReplay:true,terminalReplayRequired:true,terminalReplayScriptSha256:config.qaScriptBindings.terminalSha256,clientDeadlineChanged:false,noConcurrentOrBackgroundReplay:true},null,2));
const server=createServer(async(req,res)=>{
  const start=performance.now(),index=requestIndex++;let action=null;
  try {
    assert.equal(req.socket.remoteAddress,'127.0.0.1');
    if(req.url==='/health' && req.method==='GET') {res.writeHead(200,{'content-type':'application/json'});res.end(JSON.stringify({nonce:config.nonce,pid:process.pid,database:config.database,sourceCommit:EXPECTED_COMMIT}));return;}
    const match=/^\/api\/pos\/catalog\/import-recovery\/([^/?]+)$/.exec(req.url);assert.ok(match,'Unsupported fixture route');action=match[1];
    const buffers=[];let size=0;
    for await(const chunk of req) {size+=chunk.length;assert.ok(size<=524289,'Transport collection cap');buffers.push(chunk);}
    const bytes=Buffer.concat(buffers);const prefix=String(index).padStart(5,'0');
    writeFileSync(join(run,prefix+'.request.json'),bytes);
    let parsed;try{parsed=JSON.parse(bytes.toString('utf8'));}catch{}
    phase(action==='upload'?'upload-'+(parsed?.phase??'compatibility'):action+'-'+(parsed?.phase??'request'),{requestIndex:index});
    const headers=new Headers();for(const [key,value] of Object.entries(req.headers))if(value!==undefined)headers.set(key,Array.isArray(value)?value.join(','):value);
    const request=new Request(`http://127.0.0.1:${config.httpPort}${req.url}`,{method:req.method,headers,...(!['GET','HEAD'].includes(req.method)?{body:bytes}:{})});
    const firstRouteStart=performance.now();
    const response=await (route[req.method]??route.GET)(request,{params:Promise.resolve({action})});
    const firstRouteMs=performance.now()-firstRouteStart,firstDrainStart=performance.now();
    const responseBytes=Buffer.from(await response.arrayBuffer());
    const firstResponseDrainMs=performance.now()-firstDrainStart,firstCaptureStart=performance.now();
    writeFileSync(join(run,prefix+'.response.json'),responseBytes);
    const firstResponseCaptureMs=performance.now()-firstCaptureStart;
    let inbandReplayMs=null,responsePath='NO_ACCEPTED_POST_REPLAY';
    const retryStart=performance.now();
    const replay=await acceptedPostReplay({action,method:req.method,bytes,status:response.status,responseBytes,
      replayRoute:async immutableBytes=>{
        const retryRequest=new Request(`http://127.0.0.1:${config.httpPort}${req.url}`,{method:req.method,headers,body:immutableBytes});
        const retryResponse=await route.POST(retryRequest,{params:Promise.resolve({action})});
        return {status:retryResponse.status,bytes:Buffer.from(await retryResponse.arrayBuffer())};
      }});
    responsePath=replay.responsePath;
    if(responsePath==='SINGLE_APPLY_FIRST_RESPONSE') {
      const result=JSON.parse(responseBytes);
      assertAcceptedPart(result,parsed,{planId:config.finalPosInputBinding.planId,partCount:config.finalPosInputBinding.partCount});
      acceptedParts.add(parsed.partIndex);
      assert.equal(result.parentStatus,acceptedParts.size===config.finalPosInputBinding.partCount?'complete':'partial');
    } else if(replay.retryBytes!==null) {
      writeFileSync(join(run,prefix+'.retry.response.json'),replay.retryBytes);
      inbandReplayMs=performance.now()-retryStart;
      appendFileSync(join(run,'immutable-retries.jsonl'),JSON.stringify({index,action,requestSha256:hash(bytes),firstResponseSha256:hash(responseBytes),retryResponseSha256:hash(replay.retryBytes),requestBytesUnchanged:true,responseBytesEqual:true,durationMs:inbandReplayMs})+'\n');
    }
    const selector={};for(const key of ['phase','uploadId','manifestSha256','rawSha256','cursor','stage','offset','partIndex','planId','verifiedOriginalId'])if(parsed?.[key]!==undefined)selector[key]=parsed[key];
    schedule.push({index,action,selector,httpStatus:response.status,requestFile:prefix+'.request.json',requestSha256:hash(bytes),requestByteLength:bytes.length,responseFile:prefix+'.response.json',responseSha256:hash(responseBytes),responseByteLength:responseBytes.length,durationMs:performance.now()-start,qaReplayPolicy:QA_REPLAY_POLICY,responsePath,firstRouteMs,firstResponseDrainMs,firstResponseCaptureMs,inbandReplayMs});
    writeFileSync(join(run,'schedule.json'),JSON.stringify({schemaVersion:'win7pos-phased-admin-schedule-v2',qaReplayPolicy:QA_REPLAY_POLICY,sourceCommit:EXPECTED_COMMIT,sourceTree:EXPECTED_TREE,inputsBindingManifestSha256:config.inputsBindingManifestSha256,nonce:config.nonce,requests:schedule},null,2));
    appendFileSync(join(run,'worker-resources.jsonl'),JSON.stringify({atUtc:new Date().toISOString(),index,action,memory:process.memoryUsage(),cpuMicros:process.cpuUsage(),durationMs:performance.now()-start})+'\n');
    for(const [key,value] of response.headers)res.setHeader(key,value);res.writeHead(response.status);res.end(responseBytes);
  } catch(error) {
    appendFileSync(join(run,'worker-failures.jsonl'),JSON.stringify({index,action,message:error.message,stack:error.stack})+'\n');
    res.writeHead(500,{'content-type':'application/json'});res.end(JSON.stringify({ok:false,code:'local_fixture_failure'}));
  }
});
server.listen(config.httpPort,config.httpHost,()=>{writeFileSync(join(run,'worker.identity.json'),JSON.stringify({pid:process.pid,startedAtUtc:new Date(Date.now()-process.uptime()*1000).toISOString(),executablePath:process.execPath,scriptPath:resolve(process.argv[1]),nonce:config.nonce},null,2));phase('worker-ready');console.log(JSON.stringify({ready:true,pid:process.pid,nonce:config.nonce}));});
async function close(){server.close();await pool.end();process.exit(0);}
process.on('SIGINT',close);process.on('SIGTERM',close);
