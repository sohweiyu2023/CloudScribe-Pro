@echo off
setlocal EnableExtensions

rem CloudScribe Pro - beginner-friendly Windows build/publish launcher.
rem This file intentionally uses a process-scoped PowerShell execution-policy override
rem so a downloaded source ZIP can invoke the repository's reviewed publish script
rem without changing the user's persistent PowerShell policy.

set "ROOT=%~dp0"
for %%I in ("%ROOT%..") do set "PARENT=%%~fI"
set "OUT=%PARENT%\CloudScribe-Windows"
set "APP=%ROOT%src\CloudScribe.App\CloudScribe.App.csproj"
set "NUGET=%ROOT%NuGet.config"
set "PUBLISH_SCRIPT=%ROOT%scripts\publish-stage2-windows.ps1"

where dotnet >nul 2>nul
if errorlevel 1 goto :missing_dotnet
where pwsh >nul 2>nul
if errorlevel 1 goto :missing_pwsh
where python >nul 2>nul
if errorlevel 1 goto :missing_python

pushd "%ROOT%"
for /f "usebackq delims=" %%V in (`dotnet --version`) do set "DOTNET_VERSION=%%V"
popd
if not "%DOTNET_VERSION%"=="10.0.302" goto :wrong_dotnet

if exist "%OUT%" (
  echo Removing previous runnable output:
  echo   %OUT%
  rmdir /s /q "%OUT%"
  if exist "%OUT%" goto :remove_failed
)

echo.
echo [1/3] Restoring CloudScribe.App with the locked dependency graph...
pushd "%ROOT%"
dotnet restore "%APP%" --locked-mode --configfile "%NUGET%"
if errorlevel 1 goto :failed_pop

echo.
echo [2/3] Building CloudScribe.App Release...
dotnet build "%APP%" -c Release --no-restore
if errorlevel 1 goto :failed_pop

echo.
echo [3/3] Publishing runnable Windows output...
pwsh -NoProfile -ExecutionPolicy Bypass -File "%PUBLISH_SCRIPT%" -OutputDirectory "%OUT%" -Configuration Release -Status verification-pending
if errorlevel 1 goto :publish_failed_pop
popd

for %%F in (CloudScribe.exe CloudScribe.dll CloudScribe.deps.json CloudScribe.runtimeconfig.json appsettings.json RUN-CLOUDSCRIBE.cmd) do (
  if not exist "%OUT%\%%F" (
    echo ERROR: Publish reported success but %%F is missing.
    goto :failed
  )
)

echo.
echo ============================================================
echo BUILD AND PUBLISH SUCCEEDED
echo ============================================================
echo Executable:
echo   %OUT%\CloudScribe.exe
echo Launcher:
echo   %OUT%\RUN-CLOUDSCRIBE.cmd
echo.
if /I "%CLOUDSCRIBE_NO_OPEN%"=="1" exit /b 0

echo Opening the runnable output folder...
start "" explorer.exe "%OUT%"
exit /b 0

:publish_failed_pop
set "PUBLISH_EXIT=%ERRORLEVEL%"
popd
echo.
echo ERROR: The publish step failed with exit code %PUBLISH_EXIT%.
echo.
echo The launcher used PowerShell's process-only -ExecutionPolicy Bypass switch.
echo If Windows still says the script must be digitally signed, a Group Policy may
echo be enforcing MachinePolicy or UserPolicy. Check with:
echo   pwsh -NoProfile -Command "Get-ExecutionPolicy -List"
echo Do not weaken a managed computer's Group Policy. Ask the administrator or use
echo a trusted/signed-script workflow instead.
exit /b %PUBLISH_EXIT%

:failed_pop
set "BUILD_EXIT=%ERRORLEVEL%"
popd
echo.
echo ERROR: CloudScribe build failed with exit code %BUILD_EXIT%.
exit /b %BUILD_EXIT%

:missing_dotnet
echo ERROR: dotnet was not found. Install the .NET SDK required by global.json.
exit /b 2

:missing_pwsh
echo ERROR: pwsh was not found. Install PowerShell 7 or later.
exit /b 2

:missing_python
echo ERROR: python was not found. Python is required by the safe publish-path validator.
exit /b 2

:wrong_dotnet
echo ERROR: CloudScribe requires .NET SDK 10.0.302 for this checkpoint.
echo Found: %DOTNET_VERSION%
exit /b 2

:remove_failed
echo ERROR: Could not remove previous output folder:
echo   %OUT%
exit /b 2

:failed
exit /b 1
