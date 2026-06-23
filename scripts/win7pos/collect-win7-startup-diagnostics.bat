@echo off
setlocal EnableExtensions

set "STAMP=%DATE:/=-%_%TIME::=-%"
set "STAMP=%STAMP: =0%"
set "OUT=%USERPROFILE%\Desktop\Win7POS-startup-diagnostics-%STAMP%"
set "APPDIR=%CD%"
if exist "%~dp0..\..\Win7POS.Wpf.exe" set "APPDIR=%~dp0..\.."
if exist "%~dp0Win7POS.Wpf.exe" set "APPDIR=%~dp0"
if not "%~1"=="" set "APPDIR=%~1"
set "PROGRAMDATA_LOGS=%ProgramData%\Win7POS\logs"

mkdir "%OUT%" >nul 2>nul
mkdir "%OUT%\logs" >nul 2>nul

echo Win7POS startup diagnostics > "%OUT%\README.txt"
echo Created: %DATE% %TIME% >> "%OUT%\README.txt"
echo. >> "%OUT%\README.txt"

tasklist /FI "IMAGENAME eq Win7POS.Wpf.exe" > "%OUT%\tasklist-Win7POS.txt" 2>&1
dir "%APPDIR%" /S > "%OUT%\app-folder-dir.txt" 2>&1
dir "%PROGRAMDATA_LOGS%" /S > "%OUT%\programdata-logs-dir.txt" 2>&1

if exist "%PROGRAMDATA_LOGS%\startup-trace.log" copy /Y "%PROGRAMDATA_LOGS%\startup-trace.log" "%OUT%\logs\startup-trace.log" >nul 2>nul
if exist "%PROGRAMDATA_LOGS%\app.log" copy /Y "%PROGRAMDATA_LOGS%\app.log" "%OUT%\logs\app.log" >nul 2>nul
if exist "%APPDIR%\startup-trace.log" copy /Y "%APPDIR%\startup-trace.log" "%OUT%\logs\startup-trace-appfolder.log" >nul 2>nul
if exist "%APPDIR%\VERSION.txt" copy /Y "%APPDIR%\VERSION.txt" "%OUT%\VERSION.txt" >nul 2>nul

wevtutil qe Application /q:"*[System[(Level=1 or Level=2)]]" /c:40 /f:text > "%OUT%\eventlog-application-errors.txt" 2>&1
if errorlevel 1 (
  echo wevtutil query failed. Trying legacy eventquery. > "%OUT%\eventlog-application-errors.txt"
  eventquery.vbs /L Application /FI "Type eq Error" /V >> "%OUT%\eventlog-application-errors.txt" 2>&1
)

powershell -NoProfile -Command "Compress-Archive -Path '%OUT%\*' -DestinationPath '%OUT%.zip' -Force" > "%OUT%\zip.log" 2>&1
if errorlevel 1 (
  echo ZIP not created. Folder output remains: "%OUT%"
) else (
  echo ZIP created: "%OUT%.zip"
)

echo Diagnostics folder: "%OUT%"
endlocal
