# Remote ASUS Handoff - 2026-07-01

## Scopo

Branch temporanea per permettere a Codex ASUS di testare esattamente lo stato verificato da Codex Mac, senza trasferimento manuale di file.

## Branch

- Source local branch: audit/win7pos-full-hardening
- Remote handoff branch: handoff/win7pos-asus-qa-20260701
- Base commit: 6a21f0a

## Stato atteso ASUS

ASUS deve:

1. fare fetch della branch remota;
2. installare SDK .NET 10 se manca;
3. compilare Core/Data/CLI/WPF;
4. eseguire selftest;
5. eseguire smoke WPF reale;
6. generare release pack Windows;
7. generare installer Inno Setup se disponibile;
8. scrivere report in docs/reports/2026-07-01_ASUS_WINDOWS_QA_RESULT.md;
9. pushare una branch risultato qa/asus-win7pos-result-20260701.

## Regole

- Non usare dati produzione.
- Non inserire secret.
- Non fare commit su main.
- Non dichiarare test hardware passati se non eseguiti davvero.
