@echo off
setlocal

set "ADMIN_WEB_BASE_URL=https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev"
set "CONFIG_DIR=%ProgramData%\Win7POS"
set "CONFIG_FILE=%CONFIG_DIR%\pos-admin-web.config"

if "%ProgramData%"=="" (
  echo ERROR: ProgramData is not defined.
  exit /b 1
)

if not exist "%CONFIG_DIR%" (
  mkdir "%CONFIG_DIR%"
  if errorlevel 1 (
    echo ERROR: Cannot create "%CONFIG_DIR%".
    echo Run this helper from an elevated command prompt if Windows denies access.
    exit /b 1
  )
)

> "%CONFIG_FILE%" echo AdminWebBaseUrl=%ADMIN_WEB_BASE_URL%
if errorlevel 1 (
  echo ERROR: Cannot write "%CONFIG_FILE%".
  echo Run this helper from an elevated command prompt if Windows denies access.
  exit /b 1
)

echo Win7POS Admin Web staging URL configured.
echo URL: %ADMIN_WEB_BASE_URL%
echo File: %CONFIG_FILE%
echo Keep WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB unset for workers.dev staging.

endlocal
