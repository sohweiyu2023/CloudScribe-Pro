$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checker = Join-Path $PSScriptRoot 'Test-ThirdPartyNotices.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('cloudscribe-notices-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $app = Join-Path $testRoot 'app'
    $notices = Join-Path $testRoot 'notices'
    New-Item -ItemType Directory -Path $app,$notices | Out-Null
    $dll = Join-Path $app 'Fixture.dll'
    $notice = Join-Path $notices 'LICENSE.txt'
    [IO.File]::WriteAllText($dll, 'fixture binary identity')
    [IO.File]::WriteAllText($notice, 'fixture upstream notice')
    $deps = @{ libraries = @{ 'Fixture/1.0.0' = @{type='package'} } }
    $deps | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $app 'CloudScribe.deps.json') -Encoding utf8
    $inventory = @{
        schema_version=1
        first_party_dlls=@('CloudScribe.dll','CloudScribe.Application.dll','CloudScribe.Domain.dll','CloudScribe.Infrastructure.dll','CloudScribe.Providers.Abstractions.dll')
        components=@(@{package='Fixture';version='1.0.0';distributed_files=@(@{path='Fixture.dll';sha256=(Get-FileHash $dll).Hash});notice_files=@(@{path='LICENSE.txt';sha256=(Get-FileHash $notice).Hash})})
    }
    $inventoryPath = Join-Path $notices 'components.json'
    function Save-Inventory { $inventory | ConvertTo-Json -Depth 12 | Set-Content $inventoryPath -Encoding utf8 }
    function Expect-Rejection([string]$Expected) {
        $message = $null
        try { & $checker -AppRoot $app -NoticesRoot $notices | Out-Null }
        catch { $message = $_.Exception.Message }
        if (-not $message -or -not $message.Contains($Expected,[StringComparison]::Ordinal)) { throw "Expected rejection '$Expected'; got '$message'" }
    }
    Save-Inventory
    & $checker -AppRoot $app -NoticesRoot $notices
    [IO.File]::AppendAllText($dll, 'changed')
    Expect-Rejection 'Changed dependency asset'
    [IO.File]::WriteAllText($dll, 'fixture binary identity')
    [IO.File]::WriteAllText($notice, 'changed notice')
    Expect-Rejection 'Changed notice'
    [IO.File]::WriteAllText($notice, 'fixture upstream notice')
    Move-Item -LiteralPath $notice -Destination ($notice + '.saved')
    Expect-Rejection 'Missing notice'
    Move-Item -LiteralPath ($notice + '.saved') -Destination $notice
    [IO.File]::WriteAllText((Join-Path $app 'Unaccounted.dll'), 'unaccounted dependency')
    Expect-Rejection 'Unaccounted redistributed dependency DLL'
    Remove-Item -LiteralPath (Join-Path $app 'Unaccounted.dll')
    $inventory.components[0].version='2.0.0'
    Save-Inventory
    Expect-Rejection 'Dependency version is absent'
    $inventory.components[0].version='1.0.0'
    $inventory.components[0].notice_files[0].path='../outside.txt'
    Save-Inventory
    Expect-Rejection 'Unsafe relative path'
    Write-Output 'PASS: valid inventory and six missing/tampered/unaccounted/version/path rejection cases.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempBase,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $resolved) -notlike 'cloudscribe-notices-*') { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
