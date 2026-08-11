[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [string]$ScratchRoot = (Join-Path $env:TEMP 'CloudScribe launcher path regression')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$specialParent = Join-Path $ScratchRoot 'Repeated Download (7) & ! literal'
$specialSource = Join-Path $specialParent 'CloudScribe'
$output = Join-Path $specialParent 'CloudScribe-Windows'

Remove-Item -LiteralPath $ScratchRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $specialParent -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $specialSource -Recurse -Force

$launcher = Join-Path $specialSource 'BUILD-CLOUDSCRIBE-WINDOWS.cmd'
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw "Launcher copy missing: $launcher"
}

$previousNoOpen = $env:CLOUDSCRIBE_NO_OPEN
try {
    $env:CLOUDSCRIBE_NO_OPEN = '1'
    & cmd.exe /d /c "`"$launcher`""
    if ($LASTEXITCODE -ne 0) {
        throw "Launcher failed from special-character path with exit code $LASTEXITCODE"
    }
}
finally {
    $env:CLOUDSCRIBE_NO_OPEN = $previousNoOpen
}

foreach ($name in @(
    'CloudScribe.exe',
    'CloudScribe.dll',
    'CloudScribe.deps.json',
    'CloudScribe.runtimeconfig.json',
    'appsettings.json',
    'RUN-CLOUDSCRIBE.cmd',
    'BUILD-STATUS.txt'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $output $name) -PathType Leaf)) {
        throw "Special-path launcher output missing: $name"
    }
}

Write-Host "CLOUDSCRIBE_WINDOWS_LAUNCHER_SPECIAL_PATH=PASS source=$specialSource"
