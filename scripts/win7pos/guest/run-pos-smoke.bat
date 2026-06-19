@echo off
setlocal

set "TEST_ROOT=C:\Win7POSTest"
set "DROP_DIR=%TEST_ROOT%\drop\Win7POS"
set "LOG_DIR=%TEST_ROOT%\logs"
set "DATA_DIR=%TEST_ROOT%\data"
set "APP_EXE=%DROP_DIR%\Win7POS.Wpf.exe"

echo Win7POS guest smoke launcher
echo.
echo Drop: %DROP_DIR%
echo Data: %DATA_DIR%
echo Logs: %LOG_DIR%
echo.

if not exist "%DROP_DIR%\" (
    echo ERROR: Drop folder not found.
    echo Expected: %DROP_DIR%
    echo Copy the host drop to C:\Win7POSTest\drop\Win7POS and run again.
    exit /b 1
)

if not exist "%APP_EXE%" (
    echo ERROR: Win7POS.Wpf.exe not found.
    echo Expected: %APP_EXE%
    echo Use the complete Windows Release net48 output or dist\Win7POS drop.
    exit /b 1
)

if not exist "%LOG_DIR%\" mkdir "%LOG_DIR%"
if not exist "%DATA_DIR%\" mkdir "%DATA_DIR%"

set "WIN7POS_DATA_DIR=%DATA_DIR%"

echo Starting Win7POS...
echo WIN7POS_DATA_DIR=%WIN7POS_DATA_DIR%
echo.
echo After closing the app, copy this log if it exists:
echo   %DATA_DIR%\logs\app.log
echo to:
echo   %LOG_DIR%\app.log
echo.

start "" "%APP_EXE%"
exit /b 0
