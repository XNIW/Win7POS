Win7POS Installer (Inno Setup) - Build Notes

Files:
- Win7POS.iss: installer script skeleton

Goal:
- Install application binaries to Program Files\Win7POS
- Keep runtime data in %ProgramData%\Win7POS untouched

Prerequisites on Windows build machine:
1) Inno Setup 6 (iscc.exe available)
2) A prepared app folder from release pack workflow, typically:
   ..\dist\Win7POS
   containing Win7POS.Wpf.exe and required DLLs

How to build installer:
1) Open installer\Win7POS.iss in Inno Setup Compiler GUI
   OR run from terminal:
   iscc installer\Win7POS.iss
2) Output setup exe is generated in:
   installer\output\Win7POS-Setup.exe

Important:
- This script does NOT write/delete data under %ProgramData%\Win7POS.
- Uninstall removes files from Program Files only.
- Data/logs/backups/exports/receipts remain in ProgramData.

Common adjustments before release:
- Update MyAppVersion in Win7POS.iss
- Update MyAppSourceDir if your dist path differs
- Verify dist contains x86 SQLite native runtime DLLs
  (for example e_sqlite3.dll or SQLite.Interop.dll depending on provider)

