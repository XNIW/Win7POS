@echo off
setlocal EnableExtensions

set "TEST_ROOT=C:\Win7POSTest"
set "DROP_DIR=%TEST_ROOT%\drop\Win7POS"
set "DATA_DIR=%TEST_ROOT%\data"
set "LOG_DIR=%DATA_DIR%\logs"
set "APP_EXE=%DROP_DIR%\Win7POS.Wpf.exe"

if "%WIN7POS_BRIDGE_ROOT%"=="" (
    set "BRIDGE_ROOT=C:\Win7POSBridge"
) else (
    set "BRIDGE_ROOT=%WIN7POS_BRIDGE_ROOT%"
)
set "BRIDGE_OUTBOX=%BRIDGE_ROOT%\outbox"

if not exist "%DATA_DIR%\" mkdir "%DATA_DIR%"
if not exist "%LOG_DIR%\" mkdir "%LOG_DIR%"
if not exist "%BRIDGE_OUTBOX%\" mkdir "%BRIDGE_OUTBOX%"

set "WIN7POS_DATA_DIR=%DATA_DIR%"
set "SMOKE_LOG=%LOG_DIR%\physical-smoke.txt"

echo Win7POS physical smoke > "%SMOKE_LOG%"
echo Started: %DATE% %TIME% >> "%SMOKE_LOG%"
echo Drop: %DROP_DIR% >> "%SMOKE_LOG%"
echo Data: %WIN7POS_DATA_DIR% >> "%SMOKE_LOG%"
echo App: %APP_EXE% >> "%SMOKE_LOG%"
echo Hardware checks: skipped by design. >> "%SMOKE_LOG%"
echo. >> "%SMOKE_LOG%"

if not exist "%APP_EXE%" (
    echo ERROR: Win7POS.Wpf.exe not found. >> "%SMOKE_LOG%"
    echo Expected: %APP_EXE% >> "%SMOKE_LOG%"
    copy /Y "%SMOKE_LOG%" "%BRIDGE_OUTBOX%\physical-smoke.txt" >nul
    type "%SMOKE_LOG%"
    exit /b 1
)

echo Starting Win7POS.Wpf.exe... >> "%SMOKE_LOG%"
start "" "%APP_EXE%"

echo Waiting 5 seconds before tasklist check... >> "%SMOKE_LOG%"
ping -n 6 127.0.0.1 >nul

echo Running tasklist check... >> "%SMOKE_LOG%"
tasklist | findstr /I "Win7POS" >> "%SMOKE_LOG%" 2>&1
set "TASKLIST_STATUS=%ERRORLEVEL%"

if "%TASKLIST_STATUS%"=="0" (
    echo OK: Win7POS process found. >> "%SMOKE_LOG%"
) else (
    echo ERROR: Win7POS process not found after launch wait. >> "%SMOKE_LOG%"
)

echo Finished: %DATE% %TIME% >> "%SMOKE_LOG%"
copy /Y "%SMOKE_LOG%" "%BRIDGE_OUTBOX%\physical-smoke.txt" >nul
type "%SMOKE_LOG%"
exit /b %TASKLIST_STATUS%
