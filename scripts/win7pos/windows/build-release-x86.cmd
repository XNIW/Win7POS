@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "PS1=%SCRIPT_DIR%build-release-x86.ps1"

echo Win7POS Windows Builder
echo.
echo This wrapper runs:
echo   %PS1%
echo.
echo Use -DryRun for a non-build check:
echo   build-release-x86.cmd -DryRun
echo.

if not exist "%PS1%" (
    echo ERROR: PowerShell script not found.
    echo Expected: %PS1%
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
    echo.
    echo Build script failed with exit code %EXIT_CODE%.
) else (
    echo.
    echo Build script finished.
)

if "%1"=="" (
    echo.
    pause
)

exit /b %EXIT_CODE%
