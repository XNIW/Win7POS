@echo off
setlocal EnableExtensions

if "%WIN7POS_BRIDGE_ROOT%"=="" (
    set "BRIDGE_ROOT=C:\Win7POSBridge"
) else (
    set "BRIDGE_ROOT=%WIN7POS_BRIDGE_ROOT%"
)

set "INBOX=%BRIDGE_ROOT%\inbox"
set "OUTBOX=%BRIDGE_ROOT%\outbox"
set "DONE=%BRIDGE_ROOT%\done"
set "FAILED=%BRIDGE_ROOT%\failed"
set "BRIDGE_LOGS=%BRIDGE_ROOT%\logs"
set "SCREENSHOTS=%BRIDGE_ROOT%\screenshots"
set "DROP=%BRIDGE_ROOT%\drop"

call :ensure_dirs
if errorlevel 1 exit /b 1

echo Win7POS physical Windows 7 bridge
echo Bridge root: %BRIDGE_ROOT%
echo Inbox: %INBOX%
echo Outbox: %OUTBOX%
echo.
echo Allowed jobs:
echo   env-report.job
echo   smoke-pos.job
echo   tasklist.job
echo   collect-logs.job
echo.
echo Drop a .job file in inbox. Press Ctrl+C to stop.
echo WARNING: job execution has no built-in timeout. Monitor this console;
echo if a job hangs, stop the bridge with Ctrl+C and move the job manually.
echo.

:loop
rem Jobs are allowlisted and run synchronously. Windows 7 cmd.exe has no
rem reliable built-in per-call timeout; operators must monitor this bridge.
call :process_job env-report.job env-report
call :process_job smoke-pos.job smoke-pos
call :process_job tasklist.job tasklist
call :process_job collect-logs.job collect-logs
ping -n 4 127.0.0.1 >nul
goto loop

:ensure_dirs
if not exist "%BRIDGE_ROOT%\" mkdir "%BRIDGE_ROOT%" || exit /b 1
if not exist "%INBOX%\" mkdir "%INBOX%" || exit /b 1
if not exist "%OUTBOX%\" mkdir "%OUTBOX%" || exit /b 1
if not exist "%DONE%\" mkdir "%DONE%" || exit /b 1
if not exist "%FAILED%\" mkdir "%FAILED%" || exit /b 1
if not exist "%BRIDGE_LOGS%\" mkdir "%BRIDGE_LOGS%" || exit /b 1
if not exist "%SCREENSHOTS%\" mkdir "%SCREENSHOTS%" || exit /b 1
if not exist "%DROP%\" mkdir "%DROP%" || exit /b 1
exit /b 0

:timestamp
set "BRIDGE_TS="
for /f "tokens=2 delims==" %%I in ('wmic os get localdatetime /value 2^>nul ^| find "="') do set "BRIDGE_TS=%%I"
if not "%BRIDGE_TS%"=="" (
    set "BRIDGE_TS=%BRIDGE_TS:~0,8%-%BRIDGE_TS:~8,6%"
) else (
    set "BRIDGE_TS=%DATE%-%TIME%"
)
set "BRIDGE_TS=%BRIDGE_TS:/=-%"
set "BRIDGE_TS=%BRIDGE_TS:\=-%"
set "BRIDGE_TS=%BRIDGE_TS::=%"
set "BRIDGE_TS=%BRIDGE_TS:.=%"
set "BRIDGE_TS=%BRIDGE_TS:,=%"
set "BRIDGE_TS=%BRIDGE_TS: =0%"
exit /b 0

:process_job
set "JOB_FILE=%~1"
set "JOB_LABEL=%~2"
set "JOB_PATH=%INBOX%\%JOB_FILE%"
if not exist "%JOB_PATH%" exit /b 0

call :timestamp
set "JOB_LOG=%OUTBOX%\%BRIDGE_TS%-%JOB_LABEL%.log"

echo === Win7POS bridge job === > "%JOB_LOG%"
echo Job: %JOB_FILE% >> "%JOB_LOG%"
echo Started: %DATE% %TIME% >> "%JOB_LOG%"
echo Bridge root: %BRIDGE_ROOT% >> "%JOB_LOG%"
echo. >> "%JOB_LOG%"

if /I "%JOB_LABEL%"=="env-report" goto run_env_report
if /I "%JOB_LABEL%"=="smoke-pos" goto run_smoke_pos
if /I "%JOB_LABEL%"=="tasklist" goto run_tasklist
if /I "%JOB_LABEL%"=="collect-logs" goto run_collect_logs

echo ERROR: unsupported job label %JOB_LABEL% >> "%JOB_LOG%"
set "JOB_STATUS=1"
goto job_completed

:run_env_report
call :job_env_report >> "%JOB_LOG%" 2>&1
set "JOB_STATUS=%ERRORLEVEL%"
goto job_completed

:run_smoke_pos
call :job_smoke_pos >> "%JOB_LOG%" 2>&1
set "JOB_STATUS=%ERRORLEVEL%"
goto job_completed

:run_tasklist
call :job_tasklist >> "%JOB_LOG%" 2>&1
set "JOB_STATUS=%ERRORLEVEL%"
goto job_completed

:run_collect_logs
call :job_collect_logs >> "%JOB_LOG%" 2>&1
set "JOB_STATUS=%ERRORLEVEL%"
goto job_completed

:job_completed

echo. >> "%JOB_LOG%"
echo Finished: %DATE% %TIME% >> "%JOB_LOG%"
echo Exit code: %JOB_STATUS% >> "%JOB_LOG%"

if "%JOB_STATUS%"=="0" (
    echo Moving job to done. >> "%JOB_LOG%"
    move /Y "%JOB_PATH%" "%DONE%\%BRIDGE_TS%-%JOB_FILE%" >> "%JOB_LOG%" 2>&1
) else (
    echo Moving job to failed. >> "%JOB_LOG%"
    move /Y "%JOB_PATH%" "%FAILED%\%BRIDGE_TS%-%JOB_FILE%" >> "%JOB_LOG%" 2>&1
)

exit /b 0

:job_env_report
echo Job: env-report
echo Computer: %COMPUTERNAME%
echo OS:
ver
echo Processor architecture: %PROCESSOR_ARCHITECTURE%
echo Bridge root: %BRIDGE_ROOT%
echo Test root: C:\Win7POSTest
echo Drop expected: C:\Win7POSTest\drop\Win7POS\Win7POS.Wpf.exe
echo.
echo Bridge folders:
dir "%BRIDGE_ROOT%" /AD
exit /b 0

:job_smoke_pos
set "SMOKE_SCRIPT=%~dp0run-physical-smoke.bat"
echo Job: smoke-pos
if not exist "%SMOKE_SCRIPT%" (
    echo ERROR: run-physical-smoke.bat not found beside bridge script.
    echo Expected: %SMOKE_SCRIPT%
    exit /b 1
)
call "%SMOKE_SCRIPT%"
exit /b %ERRORLEVEL%

:job_tasklist
echo Job: tasklist
tasklist
exit /b %ERRORLEVEL%

:job_collect_logs
echo Job: collect-logs
set "TEST_ROOT=C:\Win7POSTest"
set "TEST_DATA_LOGS=%TEST_ROOT%\data\logs"
set "TEST_LOGS=%TEST_ROOT%\logs"
set "TEST_SCREENSHOTS=%TEST_ROOT%\screenshots"
set "COLLECTED=0"

if exist "%TEST_DATA_LOGS%\*.log" copy /Y "%TEST_DATA_LOGS%\*.log" "%BRIDGE_LOGS%\" && set "COLLECTED=1"
if exist "%TEST_DATA_LOGS%\*.txt" copy /Y "%TEST_DATA_LOGS%\*.txt" "%BRIDGE_LOGS%\" && set "COLLECTED=1"
if exist "%TEST_LOGS%\*.log" copy /Y "%TEST_LOGS%\*.log" "%BRIDGE_LOGS%\" && set "COLLECTED=1"
if exist "%TEST_LOGS%\*.txt" copy /Y "%TEST_LOGS%\*.txt" "%BRIDGE_LOGS%\" && set "COLLECTED=1"
if exist "%TEST_SCREENSHOTS%\*.png" copy /Y "%TEST_SCREENSHOTS%\*.png" "%SCREENSHOTS%\" && set "COLLECTED=1"
if exist "%TEST_SCREENSHOTS%\*.jpg" copy /Y "%TEST_SCREENSHOTS%\*.jpg" "%SCREENSHOTS%\" && set "COLLECTED=1"
if exist "%TEST_SCREENSHOTS%\*.jpeg" copy /Y "%TEST_SCREENSHOTS%\*.jpeg" "%SCREENSHOTS%\" && set "COLLECTED=1"
if exist "%TEST_SCREENSHOTS%\*.bmp" copy /Y "%TEST_SCREENSHOTS%\*.bmp" "%SCREENSHOTS%\" && set "COLLECTED=1"

echo.
echo Bridge logs:
dir "%BRIDGE_LOGS%"
echo.
echo Bridge screenshots:
dir "%SCREENSHOTS%"
if "%COLLECTED%"=="0" (
    echo ERROR: no logs or screenshots were collected.
    exit /b 1
)
exit /b 0
