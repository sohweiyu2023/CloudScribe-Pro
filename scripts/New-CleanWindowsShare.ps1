param(
    [Parameter(Mandatory=$true)][string]$PublishedApp,
    [Parameter(Mandatory=$true)][string]$RuntimeRoot,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath $PublishedApp).Path
$runtime = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$target = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $target) { throw 'Choose a new output directory; existing files are never replaced.' }
$settings = Get-Content -LiteralPath (Join-Path $app 'appsettings.json') -Raw | ConvertFrom-Json
if ($settings.CloudScribe.AppDataDirectoryOverride) { throw 'Use a clean publish with no personal workspace override.' }
foreach ($required in @('CloudScribe.exe','CloudScribe.dll','CloudScribe.deps.json','CloudScribe.runtimeconfig.json','appsettings.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $required))) { throw "Missing published application file: $required" }
}
if (-not (Test-Path -LiteralPath (Join-Path $runtime 'shared\Microsoft.NETCore.App'))) { throw 'A compatible private .NET runtime is required.' }
$repo = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Test-ThirdPartyNotices.ps1') -AppRoot $app -NoticesRoot (Join-Path $repo 'licenses/third-party')
New-Item -ItemType Directory -Path (Join-Path $target 'app'), (Join-Path $target 'runtime') -Force | Out-Null
Get-ChildItem -LiteralPath $app -File | Where-Object {
    $_.Extension -eq '.dll' -or $_.Name -in @('CloudScribe.exe','CloudScribe.deps.json','CloudScribe.runtimeconfig.json','appsettings.json')
} | Copy-Item -Destination (Join-Path $target 'app')
foreach ($platform in @('win','win-x64')) {
    $source = Join-Path $app "runtimes\$platform"
    Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object {$_.Extension -eq '.dll'} | ForEach-Object {
        $destination = Join-Path $target ('app\runtimes\' + $platform + '\' + $_.FullName.Substring($source.Length + 1))
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
}
foreach ($name in @('dotnet.exe','LICENSE.txt','ThirdPartyNotices.txt')) {
    Copy-Item -LiteralPath (Join-Path $runtime $name) -Destination (Join-Path $target "runtime\$name")
}
New-Item -ItemType Directory -Path (Join-Path $target 'runtime\host'), (Join-Path $target 'runtime\shared') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $runtime 'host\fxr') -Destination (Join-Path $target 'runtime\host\fxr') -Recurse
Copy-Item -LiteralPath (Join-Path $runtime 'shared\Microsoft.NETCore.App') -Destination (Join-Path $target 'runtime\shared\Microsoft.NETCore.App') -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'), (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination $target
Copy-Item -LiteralPath (Join-Path $repo 'licenses') -Destination (Join-Path $target 'licenses') -Recurse
@'
@echo off
setlocal
set "DOTNET_ROOT=%~dp0runtime"
set "DOTNET_ROOT_X64=%DOTNET_ROOT%"
set "DOTNET_MULTILEVEL_LOOKUP=0"
start "" "%~dp0app\CloudScribe.exe"
'@ | Set-Content -LiteralPath (Join-Path $target 'START-CLOUDSCRIBE.cmd') -Encoding ascii
@'
CloudScribe Pro — Windows x64

Extract the whole folder, then double-click START-CLOUDSCRIBE.cmd.
Quick Start appears for a fresh profile; Help opens it again later.
Use YOUR OWN Google project, service-account key and billing account to make audio.

This folder contains app files and a private .NET runtime. Never add or share an
app-data profile, database, personal document, MP3, service-account JSON key or
Windows credentials. Each person must supply their own provider access.

Official setup: https://cloud.google.com/text-to-speech/docs/before-you-begin
Official pricing: https://cloud.google.com/text-to-speech/pricing
'@ | Set-Content -LiteralPath (Join-Path $target 'README-FIRST.txt') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-CloudScribe.ps1') -Destination (Join-Path $target 'INSTALL-CLOUDSCRIBE.ps1')
$longAudioTools=Join-Path $target 'app/tools/long-audio'
New-Item -ItemType Directory -Path $longAudioTools -Force|Out-Null
foreach($helper in @('Provisioning.Files.ps1','Replacement.Scope.ps1','Deferred.Scope.ps1','Provisioning.Scope.ps1','Provisioning.Transaction.ps1','BuildRebind.Validation.ps1','BuildRebind.Transaction.ps1')){
    Copy-Item -LiteralPath (Join-Path $repo ('tools/long-audio/'+$helper)) -Destination (Join-Path $longAudioTools $helper)
}
$articleTools=Join-Path $target 'app/tools/article-admission'
New-Item -ItemType Directory -Path $articleTools -Force|Out-Null
foreach($helper in @('Admission.Validation.ps1','Admission.Transaction.ps1','BuildRebind.ps1')){
    Copy-Item -LiteralPath (Join-Path $repo ('tools/article-admission/'+$helper)) -Destination (Join-Path $articleTools $helper)
}
foreach ($guide in @('USER_GUIDE.md','SHARING_WINDOWS.md','SAVED_SETUPS_AND_LOCAL_RECOVERY.md','VISUAL_WALKTHROUGH.md','ABOUT_REFERENCE.md','WINDOWS_INSTALLATION.md','VOICE_RECOMMENDATIONS.md','VOICE_COMPARISON.md','ARTICLE_MP3_EXPORT.md','STARTUP_DIAGNOSTICS.md','GOOGLE_FAILURE_DIAGNOSTICS.md','CHIRP_ARTICLE_NARRATION.md','COMPLETION_WORKFLOW_GATE.md','GOOGLE_LONG_AUDIO.md','BATCH_NARRATION.md','LONG_AUDIO_BUDGET_RECOVERY.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "..\docs\$guide") -Destination (Join-Path $target $guide)
}
$files = Get-ChildItem -LiteralPath $target -Recurse -File
& (Join-Path $PSScriptRoot 'Test-ThirdPartyNotices.ps1') -AppRoot (Join-Path $target 'app') -NoticesRoot (Join-Path $target 'licenses/third-party')
$unexpected = $files | Where-Object { $_.Extension -in @('.db','.mp3','.pdb') -or $_.Name -like '*.previous' }
if ($unexpected) { throw 'Unexpected data or debug files in package; do not distribute it.' }
& (Join-Path $target 'INSTALL-CLOUDSCRIBE.ps1') -PackageRoot $target -ValidatePackageOnly | Out-Null
Write-Output "Clean Windows x64 package: $target ($($files.Count) files). No profile or credential was copied."
