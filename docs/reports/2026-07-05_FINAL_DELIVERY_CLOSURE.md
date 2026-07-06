# Win7POS + Admin Web delivery closure

Data esecuzione: 2026-07-06 UTC / 2026-07-05 host locale
Branch Win7POS: `fix/win7pos-hardening-phase3`
Branch Admin Web: `fix/pos-catalog-import-sync-api`

## Stato PR

| Repo | PR | Stato | Evidenza |
| --- | --- | --- | --- |
| Win7POS | https://github.com/XNIW/Win7POS/pull/1 | `OPEN`, `MERGEABLE` | CI `build-and-check` success; Release Pack success; head pre-report `00722df8a5792dcf93245508974836be58fe0e1d`. |
| Admin Web | https://github.com/XNIW/merchandise-control-admin-web/pull/2 | `OPEN`, `MERGEABLE` | CI `Verify` success; Cloudflare build success; deploy staging manual success run `28761814972`; head pre-report `d97587f302f2177b3c8ffad6beca37b4ce614afe`. |

## Chiusura dei 5 gate

| Gate iniziale | Azione fatta | Evidenza | Stato aggiornato |
| --- | --- | --- | --- |
| `WIN7_HARDWARE_NOT_AVAILABLE` | Cercati Hyper-V, VirtualBox, VMware e path VM locali; validato lo stesso artifact GitHub su ASUS Windows 11. | Nessuna VM Win7 disponibile; ASUS `ASUSTeK COMPUTER INC. ASUS Zenbook 14 UX3405CA_UX3405CA`, Windows `10.0.26200`; prereq `ALL PASS`; artifact GitHub validati. | `HARDWARE_REQUIRED_WITH_ARTIFACT_AND_CHECKLIST` |
| `STAGING_SUPABASE_URL_MISSING` | Verificate variabili GitHub repo/env e secrets Cloudflare per staging senza stampare valori. | Project ref `jpgoimipbothfgkokyvm`; host `jpgoimipbothfgkokyvm.supabase.co`; worker staging configurato. | `CONFIGURED` |
| `SUPABASE_CLI_MISSING` | Installata Supabase CLI via npm globale; corretto tooling Windows per risolvere shim `.cmd`. | `supabase 2.109.0`; `node scripts/check-supabase-tooling.mjs` `PASS`. | `PASS` |
| `STAGING_DEPLOY_PERMISSION_MISSING` | Riaperta auth Cloudflare; eseguito deploy staging via GitHub Actions workflow dispatch. | Run https://github.com/XNIW/merchandise-control-admin-web/actions/runs/28761814972 `success`; endpoint staging smoke 405/400/401 con `no-store`. | `PASS` |
| `E2E_STAGING_PENDING` | Deploy reale e smoke negativi eseguiti; CLI/harness locali passati; migration/E2E positivo fermo solo su auth Supabase owner e sessione POS staging. | `GET /api/pos/catalog/import-sync` -> 405; `POST {}` -> 400; auth invalida -> 401 `auth_denied`; CLI local harness `CATALOG IMPORT SYNC HTTP HARNESS PASS`. | `READY_FOR_STAGING_E2E_AFTER_OWNER_SECRET` |

## Supabase staging

| Voce | Stato | Evidenza |
| --- | --- | --- |
| Project ref | `CONFIGURED` | `jpgoimipbothfgkokyvm` presente in GitHub variables e Cloudflare staging env. |
| URL staging | `CONFIGURED_MASKED` | Host `jpgoimipbothfgkokyvm.supabase.co`; chiavi pubbliche/secret non riportate. |
| CLI | `PASS` | `supabase 2.109.0`; script repo aggiornato per Windows e validato. |
| Migration TASK-094 | `SUPABASE_OWNER_PERMISSION_REQUIRED` | `supabase projects list` prima del login ha richiesto auth; `supabase login --no-browser --output-format text` avviato e in attesa di codice verifica. |
| Policy/RLS | `STATIC_PASS` | Migration `20260705120000_task_094_pos_catalog_import_sync.sql` crea `public.pos_catalog_import_batches`, forza RLS, revoca `public`/`anon`/`authenticated`, concede solo a `service_role`. |

Comando da rieseguire dopo auth Supabase:

```powershell
supabase projects list
supabase link --project-ref jpgoimipbothfgkokyvm
supabase migration list --linked
supabase db push --linked
```

## Deploy staging

| Voce | Stato | Evidenza |
| --- | --- | --- |
| Tool | `PASS` | GitHub CLI autenticata come owner repo; Wrangler autenticato su account Cloudflare autorizzato; Cloudflare secrets staging presenti per nome. |
| Deploy | `PASS` | Workflow `cloudflare.yml`, target `staging`, run `28761814972`, conclusion `success`. |
| URL | `PASS` | `https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev` |
| Smoke base endpoint | `PASS` | `GET /api/pos/catalog/import-sync` -> `405 method_not_allowed`, `cache-control: no-store`, request id `posreq_153aec65-55c8-4313-9222-67c134cd293f`. |
| Smoke validation | `PASS` | `POST {}` -> `400 validation_failed`, request id `posreq_00881edd-a623-4943-86b7-60d205ff4818`. |
| Smoke auth denied | `PASS` | Payload sintetico con auth invalida -> `401 auth_denied`, request id `posreq_4e7c29e9-1cd3-43d9-9f55-3b6392e6c379`. |

## E2E staging

| Caso | Stato | Evidenza |
| --- | --- | --- |
| accepted | `PENDING_SUPABASE_OWNER_AUTH` | Richiede migration applicata, seed/sessione POS staging e verifica Supabase. |
| duplicate/idempotent | `PENDING_SUPABASE_OWNER_AUTH` | Richiede stesso batch reale contro staging. |
| conflict | `PENDING_SUPABASE_OWNER_AUTH` | Richiede batch reale con stessa idempotency key e payload diverso. |
| auth_denied | `PASS_STAGING_SMOKE` | Endpoint staging ha risposto `401 auth_denied` senza stampare token. |
| catalog pull | `PENDING_SUPABASE_OWNER_AUTH` | Richiede prodotto/prezzo creati in staging e sessione POS valida. |

## Win7 / ASUS / artifact

| Voce | Stato | Evidenza |
| --- | --- | --- |
| VM Win7 | `HARDWARE_REQUIRED_WITH_ARTIFACT_AND_CHECKLIST` | `Get-VM`, `VBoxManage`, `vmrun` e path VM locali non disponibili. |
| ASUS Windows 11 prereq | `PASS` | `check-win7-prereqs.ps1 -AppDir C:\Temp\Win7POS-gh-artifacts\Win7POS-dist` -> `ALL PASS`; .NET 4.8+, VC++ x86, x86 PE e file pack OK. |
| ASUS startup smoke | `PASS_WITH_NOTE` | L'eseguibile artifact resta avviabile; un'istanza Win7POS preesistente ha attivato il single-instance guard, quindi il run pulito richiede chiudere l'istanza `C:\Dev\Win7POS_TASK090_QA\...\Win7POS.Wpf.exe`. |
| ReleasePack artifact | `PASS` | Artifact id `8097385015`, digest `sha256:3251459a3bd086dbd9fdcd50f66e7ba5a102b4aef49b84a7362aecf7d1611493`; zip scaricato `C:\Temp\Win7POS-gh-artifacts\Win7POS-ReleasePack-x86\Win7POS_20260706_0036.zip`, SHA256 `B94D0D4E07BA4F1BCA902C5F3448D75FB5D0A5EB7606B9B72502F53D32CFA5B6`. |
| Dist artifact | `PASS` | Artifact id `8097384847`, digest `sha256:b17c6e414dfad7e8c7bed17f06797ab455b1acb9f3f1f06cd0561e7f93eb0418`; pack validators `ALL PASS`. |
| Installer artifact | `PASS` | Artifact id `8097384647`, digest `sha256:3681c6ab088f8466710204cbd1634c12ba98e7b071d23fa7f5858ba75a65297f`; exe scaricato `C:\Temp\Win7POS-gh-artifacts\Win7POS-Setup\Win7POS-Setup.exe`, SHA256 `A118220703519660CAA1BB521A77897AF8259E3639ACB5D09BB1B96E4CD2AEC1`. |

Checklist Win7 da rieseguire sul target fisico: `docs\WIN7_PRODUCTION_SMOKE_CHECKLIST.md`.

## Installer / VC++ x86

| Voce | Stato | Evidenza |
| --- | --- | --- |
| Inno Setup | `PASS` | `ISCC.exe` trovato in `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`; artifact GitHub `Win7POS-Setup` prodotto e scaricato. |
| Installer | `PASS` | `Win7POS-Setup.exe` artifact GitHub validato con hash sopra. |
| VC++ x86 policy | `PASS` | `check-win7-prereqs.ps1` fallisce se manca VC++ x86; installer contiene check VC++ x86; README/checklist dichiarano prerequisito. |

## Test finali

| Area | Comando | Stato |
| --- | --- | --- |
| Win7POS diff | `git diff --check` | `PASS` |
| Win7POS restore | `C:\Dev\dotnet10\dotnet.exe restore Win7POS.slnx` | `PASS` |
| Win7POS solution build | `C:\Dev\dotnet10\dotnet.exe build Win7POS.slnx -c Release --no-restore` | `PASS` |
| Win7POS WPF x86 | `C:\Dev\dotnet10\dotnet.exe build src\Win7POS.Wpf\Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86 --no-restore` | `PASS` |
| Core tests | `C:\Dev\dotnet10\dotnet.exe test tests\Win7POS.Core.Tests\Win7POS.Core.Tests.csproj -c Release --no-restore` | `PASS`, 11 passed |
| CLI harnesses | selftest, supplier excel, apply, outbox, catalog HTTP, perf 20000/5000, sqlite integrity, restore guard | `PASS` |
| Win7POS check scripts | `Get-ChildItem scripts -Filter check-*.ps1 ...` | `PASS`, all scripts completed |
| Admin diff | `git diff --check` | `PASS` |
| Admin security | `npm run security:scan` | `PASS`, external Win7POS macOS path skipped as non-blocking |
| Admin foundation | `npm run test:foundation` | `PASS` |
| Admin typecheck | `npm run typecheck` | `PASS` |
| Admin lint | `npm run lint` | `PASS` |
| Admin build | `npm run build` | `PASS` |
| Admin verify | `npm run verify` | `PASS` |
| Cloudflare local build | `npm run cf:build` | `PASS_TOOLING_FIX_WITH_HOST_LIMIT`: OpenNext starts and builds Next; stops only on Windows symlink `EPERM`, while GitHub Actions Linux deploy is `PASS`. |

## Residui veri

| Residuo | Comando gia provato | Errore/stato preciso | Chi deve fare cosa | Comando successivo |
| --- | --- | --- | --- | --- |
| `SUPABASE_OWNER_PERMISSION_REQUIRED` | `supabase projects list`; `supabase login --no-browser --output-format text` | Prima del login: `LegacyPlatformAuthRequiredError`; login no-browser in attesa codice verifica. | Owner deve completare login Supabase CLI e fornire/inserire codice nel prompt sicuro. | `supabase projects list`; `supabase link --project-ref jpgoimipbothfgkokyvm`; `supabase db push --linked`. |
| `WIN7_PHYSICAL_MACHINE_REQUIRED` | `Get-VM`, `VBoxManage list vms`, `vmrun list`, lookup path VM | Hyper-V/VirtualBox/VMware/path VM non disponibili in questa sessione. | Owner deve fornire VM o macchina Windows 7 SP1 con .NET 4.8 e VC++ x86. | Copiare artifact GitHub e lanciare `scripts\win7-smoke\check-win7-prereqs.ps1`, poi checklist Win7. |
| `PRODUCTION_MERGE_AWAITING_OWNER_APPROVAL` | PR aperte e mergeable | Nessun merge eseguito per istruzione esplicita. | Owner deve approvare merge finale. | Merge PR #1 e PR #2 dopo E2E/Win7 fisico se richiesti. |

## Stato finale

`READY_FOR_STAGING_E2E_AFTER_OWNER_SECRET`

Il deploy staging e gli artifact reali sono pronti; l'unico gate funzionale non chiuso end-to-end e il positivo Supabase staging, che richiede completamento auth owner e sessione POS staging.

## Addendum ultra-finale 2026-07-06

Stato branch prima di questo addendum documentale:

- Win7POS `fix/win7pos-hardening-phase3`: `46fcd681fcb74fc66cdca3979b8e350172430fb3`.
- Admin Web `fix/pos-catalog-import-sync-api`: `1ab0f61075862f1256e157926d1c5e8e12dd541d`.

### Cloudflare staging live route

Il workflow Cloudflare staging e stato rieseguito da GitHub Actions per sostituire una versione live successiva che rispondeva `404` solo su `/api/pos/catalog/import-sync`, pur avendo gli altri endpoint POS attivi.

- Run GitHub Actions: `28763455797`.
- Build job: `85283095902`, `success`.
- Deploy staging job: `85283293516`, `success`.
- Worker staging URL: `https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev`.
- Current Version ID: `2daadf35-036c-491e-9ecc-944fbc4def68`.
- Evidence build: OpenNext route list include `/api/pos/catalog/import-sync`.
- Evidence staging smoke workflow: `1 passed`.

Probe live post-deploy:

| Probe | Risultato |
| --- | --- |
| `GET /api/pos/catalog/import-sync` | `405`, `cache-control: no-store`, request id `posreq_fe7908b0-9bb7-4cd6-aa15-2a833d61c41a` |
| `POST /api/pos/catalog/import-sync` con `{}` | `400 validation_failed`, `cache-control: no-store`, request id `posreq_60fcdfac-a76a-4815-bf2f-c811088ecfbf` |
| `POST /api/pos/catalog/import-sync` con payload valido e credenziali finte | `401 auth_denied`, `cache-control: no-store`, request id `posreq_63a1019e-697f-46d8-b4f5-8d2353323c8d` |

Il tentativo di redeploy locale Windows non e stato usato come evidenza finale perche `npm run cf:deploy:staging` si ferma nella fase OpenNext su symlink `EPERM`; il deploy autorevole resta quello Linux di GitHub Actions.

### Win7 physical smoke package

Creato il documento operativo:

- `docs/QA/WIN7_PHYSICAL_SMOKE_REQUEST.md`

Stato: `WIN7_PHYSICAL_MACHINE_REQUIRED_WITH_ARTIFACT_AND_SCRIPT_READY`.

La richiesta contiene target richiesto, artifact GitHub, hash, prereq command, checklist minima e lista evidenze non segrete. Il target Win7 SP1 fisico/VM resta necessario per passare da `WIN7_PHYSICAL_MACHINE_REQUIRED` a `PASS_WIN7_PHYSICAL_SMOKE`.

Artifact validati per il passaggio fisico:

| Artifact | ID | Digest |
| --- | --- | --- |
| `Win7POS-Setup` | `8098062159` | `sha256:f4fe4a6e0937738414c110caa5d7fef5cd490003781bbcd8f40198af3f37fccb` |
| `Win7POS-dist` | `8098062657` | `sha256:2dc7273ea26aa6b0c25b2817f98fce5938632403511929d9832c2d8a4dac2680` |
| `Win7POS-ReleasePack-x86` | `8098063084` | `sha256:35d39fd02d970b6d0b410e416a8fa16f4728bbc5f519379448a65520bef4a8e7` |

### Supabase owner gate

La CLI Supabase resta ferma su login owner:

- Comando avviato: `supabase login --no-browser --output-format text`.
- Stato: in attesa del verification code owner.
- Gate reale: `SUPABASE_OWNER_PERMISSION_REQUIRED`.

Dopo auth owner, i comandi successivi restano:

```powershell
supabase projects list
supabase link --project-ref jpgoimipbothfgkokyvm
supabase migration list --linked
supabase db push --linked
```

### Residui dopo addendum

| Residuo | Stato preciso |
| --- | --- |
| Supabase staging migration + E2E positivo | `SUPABASE_OWNER_PERMISSION_REQUIRED` |
| Win7 runtime fisico/VM | `WIN7_PHYSICAL_MACHINE_REQUIRED_WITH_ARTIFACT_AND_SCRIPT_READY` |
| Merge produzione | `PRODUCTION_MERGE_AWAITING_OWNER_APPROVAL` |
