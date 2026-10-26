$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installer = Join-Path $PSScriptRoot 'Install-CloudScribe.ps1'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@('\', '/'))
$runningWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$testRoot = Join-Path $tempRoot ('cloudscribe-install-package-tests-' + [guid]::NewGuid().ToString('N'))
$passed = [Collections.Generic.List[string]]::new()
$skipped = [Collections.Generic.List[string]]::new()
$junctions = [Collections.Generic.List[string]]::new()
$script:fixtureNumber = 0
$script:junctionUnavailableReason = $null

function Assert-TestPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = $testRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Fixture path escaped the test root: $full"
    }
    return $full
}

function Assert-NoReparseTree([string]$Path) {
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($Path)
    while ($pending.Count -gt 0) {
        $current = $pending.Dequeue()
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing recursive fixture cleanup with a reparse point: $current"
        }
        foreach ($item in Get-ChildItem -LiteralPath $current -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing recursive fixture cleanup with a reparse point: $($item.FullName)"
            }
            if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) }
        }
    }
}

function Remove-FixtureEntry([string]$Path) {
    $full = Assert-TestPath $Path
    $item = Get-Item -LiteralPath $full -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Use verified junction unlinking for links.' }
    if ($item.PSIsContainer) {
        Assert-NoReparseTree $full
        Remove-Item -LiteralPath $full -Recurse -Force
    }
    else { Remove-Item -LiteralPath $full -Force }
}

function Write-FixtureFile([string]$Path, [string]$Content) {
    $full = Assert-TestPath $Path
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full)) | Out-Null
    [IO.File]::WriteAllText($full, $Content)
}

function New-PackageFixture {
    $script:fixtureNumber++
    $caseRoot = Join-Path $testRoot ('case-{0:D2}' -f $script:fixtureNumber)
    $package = Join-Path $caseRoot 'package'
    [IO.Directory]::CreateDirectory((Join-Path $package 'licenses')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $package 'runtime/shared/Microsoft.NETCore.App/10.0.8')) | Out-Null
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path $package $name)
    }
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'licenses/third-party') -Destination (Join-Path $package 'licenses') -Recurse
    foreach ($name in @('app/CloudScribe.exe', 'app/CloudScribe.dll', 'runtime/dotnet.exe', 'runtime/shared/Microsoft.NETCore.App/10.0.8/Fixture.dll')) {
        Write-FixtureFile (Join-Path $package $name) 'Inert package validation fixture; never execute.'
    }
    Write-FixtureFile (Join-Path $package 'app/appsettings.json') '{"CloudScribe":{"AppDataDirectoryOverride":null}}'
    Write-FixtureFile (Join-Path $package 'README.md') 'Fixture package guide.'
    Write-FixtureFile (Join-Path $package 'START-CLOUDSCRIBE.cmd') '@rem Inert fixture; never execute.'
    Write-FixtureFile (Join-Path $package 'BUILD-NOTES.txt') 'Fixture package notes.'
    Write-FixtureFile (Join-Path $package 'INSTALL-CLOUDSCRIBE.ps1') '# Inert fixture; validation invokes the repository installer only.'
    return [pscustomobject]@{ Root = $caseRoot; Package = $package }
}

function Get-FirstNoticePath([string]$Package) {
    $inventory = Get-Content -LiteralPath (Join-Path $Package 'licenses/third-party/components.json') -Raw | ConvertFrom-Json
    return Assert-TestPath (Join-Path (Join-Path $Package 'licenses/third-party') $inventory.components[0].notice_files[0].path)
}

function Pass([string]$Label) {
    $passed.Add($Label)
    Write-Output "PASS: $Label"
}

function Expect-Valid([string]$Label, [string]$Package) {
    $result = & $installer -PackageRoot $Package -ValidatePackageOnly
    if ($result.Valid -ne $true -or $result.PackageOnly -ne $true -or $result.NoticeFiles -ne 15 -or $result.PackageFiles -lt 20) {
        throw "Unexpected package-only validation result for '$Label'."
    }
    Pass $Label
}

function Expect-Rejection([string]$Label, [string]$Package, [string]$Expected, [string]$MigrationSource, [switch]$InstallationPreflight) {
    $message = $null
    try {
        if ($InstallationPreflight) {
            & $installer -PackageRoot $Package -ValidatePackageOnly -ValidateOnly | Out-Null
        }
        elseif ($MigrationSource) {
            & $installer -PackageRoot $Package -ValidatePackageOnly -MigrateProfileFrom $MigrationSource | Out-Null
        }
        else {
            & $installer -PackageRoot $Package -ValidatePackageOnly | Out-Null
        }
    }
    catch { $message = $_.Exception.Message }
    if (-not $message -or $message.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw "Expected '$Expected' for '$Label'; got '$message'."
    }
    Pass $Label
}

function Rejection-Case([string]$Label, [scriptblock]$Mutation, [string]$Expected) {
    $fixture = New-PackageFixture
    & $Mutation $fixture.Package $fixture.Root
    Expect-Rejection $Label $fixture.Package $Expected
}

function Remove-VerifiedJunction([string]$Path) {
    $full = Assert-TestPath $Path
    $item = Get-Item -LiteralPath $full -Force
    if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.LinkType -ne 'Junction') {
        throw "Refusing to unlink an unverified junction: $full"
    }
    foreach ($target in @($item.Target)) { Assert-TestPath $target | Out-Null }
    [IO.Directory]::Delete($full, $false)
    $junctions.Remove($full) | Out-Null
}

function Reparse-Case([string]$Kind) {
    $label = "reject $Kind directory junction"
    if (-not $runningWindows) {
        $skipped.Add($label + ': Windows junctions unavailable')
        Write-Output "SKIP: $label (Windows junctions unavailable)"
        return
    }
    if ($script:junctionUnavailableReason) {
        $skipped.Add($label + ': ' + $script:junctionUnavailableReason)
        Write-Output "SKIP: $label ($script:junctionUnavailableReason)"
        return
    }
    $fixture = New-PackageFixture
    switch ($Kind) {
        'root' {
            $link = Join-Path $fixture.Root 'package-link'
            $target = $fixture.Package
            $candidate = $link
            $expected = 'Linked install/profile path is not allowed:'
        }
        'child' {
            $link = Join-Path $fixture.Package 'app/linked-directory'
            $target = Join-Path $fixture.Root 'external-child'
            [IO.Directory]::CreateDirectory($target) | Out-Null
            $candidate = $fixture.Package
            $expected = 'Package contains links.'
        }
        'ancestor' {
            $link = Join-Path $testRoot ('ancestor-link-' + [guid]::NewGuid().ToString('N'))
            $target = $fixture.Root
            $candidate = Join-Path $link 'package'
            $expected = 'Linked install/profile path is not allowed:'
        }
        default { throw "Unknown reparse case: $Kind" }
    }
    Assert-TestPath $link | Out-Null
    Assert-TestPath $target | Out-Null
    try {
        New-Item -ItemType Junction -Path $link -Target $target | Out-Null
    }
    catch {
        $creationError = $_
        if ($_.CategoryInfo.Category -notin @('PermissionDenied', 'NotImplemented', 'ResourceUnavailable') -and
            $_.Exception -isnot [UnauthorizedAccessException] -and $_.Exception -isnot [PlatformNotSupportedException] -and
            $_.Exception.Message -notmatch '(?i)access is denied|operation not permitted|not supported') { throw }
        # Some providers create an empty ordinary directory before rejecting the
        # junction operation. Inspect any partial result and unlink only that item.
        if (Test-Path -LiteralPath $link) {
            $partial = Get-Item -LiteralPath $link -Force
            if ($partial.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                $junctions.Add([IO.Path]::GetFullPath($link))
                Remove-VerifiedJunction $link
            }
            elseif ($partial.PSIsContainer) {
                Assert-TestPath $link | Out-Null
                [IO.Directory]::Delete($link, $false)
            }
            else { throw 'Unexpected partial junction fixture; cleanup was not attempted.' }
        }
        $script:junctionUnavailableReason = $creationError.Exception.Message
        $skipped.Add($label + ': ' + $script:junctionUnavailableReason)
        Write-Output "SKIP: $label ($script:junctionUnavailableReason)"
        return
    }
    $junctions.Add([IO.Path]::GetFullPath($link))
    try { Expect-Rejection $label $candidate $expected }
    finally { Remove-VerifiedJunction $link }
}

[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $fixture = New-PackageFixture
    Expect-Valid 'accept complete package and permitted app/runtime/root files' $fixture.Package
    $fixture = New-PackageFixture
    Expect-Valid 'accept package path with trailing separator' ($fixture.Package + [IO.Path]::DirectorySeparatorChar)

    foreach ($required in @('app/CloudScribe.exe', 'app/CloudScribe.dll', 'app/appsettings.json', 'runtime/dotnet.exe', 'runtime/shared/Microsoft.NETCore.App', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses/third-party/components.json')) {
        $fixture = New-PackageFixture
        Remove-FixtureEntry (Join-Path $fixture.Package $required)
        Expect-Rejection "reject missing $required" $fixture.Package 'Incomplete clean package:'
    }
    Rejection-Case 'reject missing inventory-listed notice' {
        param($package)
        Remove-FixtureEntry (Get-FirstNoticePath $package)
    } 'Missing third-party notice:'
    Rejection-Case 'reject changed notice bytes' {
        param($package)
        [IO.File]::AppendAllText((Get-FirstNoticePath $package), 'Tampered local fixture.')
    } 'Changed third-party notice:'
    Rejection-Case 'reject credential-shaped JSON renamed as LICENSE' {
        param($package)
        Write-FixtureFile (Join-Path $package 'LICENSE') '{"type":"service_account","private_key":"FAKE-TEST-ONLY"}'
    } 'Unexpected project license identity.'
    Rejection-Case 'reject changed notice hash in inventory' {
        param($package)
        $path = Join-Path $package 'licenses/third-party/components.json'
        $inventory = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $inventory.components[0].notice_files[0].sha256 = '0' * 64
        $inventory | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding utf8
    } 'Unexpected third-party inventory identity.'

    foreach ($extra in @('licenses/third-party/extra.json', 'licenses/third-party/extra.ps1', 'licenses/third-party/nested/extra.txt')) {
        $fixture = New-PackageFixture
        Write-FixtureFile (Join-Path $fixture.Package $extra) 'Unexpected inert fixture.'
        Expect-Rejection "reject extra notice entry $extra" $fixture.Package 'Unexpected package entry:'
    }
    foreach ($location in @('components.json', 'app/components.json', 'runtime/components.json')) {
        $fixture = New-PackageFixture
        Copy-Item -LiteralPath (Join-Path $fixture.Package 'licenses/third-party/components.json') -Destination (Join-Path $fixture.Package $location)
        $expected = if ($location -eq 'components.json') { 'Unexpected package entry:' } else { 'Unexpected JSON file;' }
        Expect-Rejection "reject spoof inventory at $location" $fixture.Package $expected
    }
    foreach ($privateEntry in @('app/private.key', 'app/profile.db')) {
        $fixture = New-PackageFixture
        Write-FixtureFile (Join-Path $fixture.Package $privateEntry) 'Fake data only; no credential or user data.'
        Expect-Rejection "reject private entry $privateEntry" $fixture.Package 'Package contains private, diagnostic or debug files;'
    }
    Rejection-Case 'reject personal profile override' {
        param($package, $caseRoot)
        @{ CloudScribe = @{ AppDataDirectoryOverride = (Join-Path $caseRoot 'unused-profile') } } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'app/appsettings.json') -Encoding utf8
    } 'The source package must not contain a personal profile override.'
    foreach ($unsafePath in @('../outside.txt', 'C:\outside\notice.txt', '\\server\share\notice.txt', 'texts/notice.txt:secret', 'texts/../outside.txt', 'texts/./notice.txt')) {
        $fixture = New-PackageFixture
        $path = Join-Path $fixture.Package 'licenses/third-party/components.json'
        $inventory = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $inventory.components[0].notice_files[0].path = $unsafePath
        $inventory | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding utf8
        Expect-Rejection "reject inventory path $unsafePath before parsing" $fixture.Package 'Unexpected third-party inventory identity.'
    }
    Rejection-Case 'reject inventory declaring credential JSON with matching content hash' {
        param($package)
        $credentialPath = Join-Path $package 'licenses/third-party/texts/credential.json'
        Write-FixtureFile $credentialPath '{"type":"service_account","private_key":"FAKE-TEST-ONLY"}'
        $path = Join-Path $package 'licenses/third-party/components.json'
        $inventory = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $inventory.components[0].notice_files[0].path = 'texts/credential.json'
        $inventory.components[0].notice_files[0].sha256 = (Get-FileHash -LiteralPath $credentialPath -Algorithm SHA256).Hash
        $inventory | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding utf8
    } 'Unexpected third-party inventory identity.'
    $fixture = New-PackageFixture
    Expect-Rejection 'reject package-only validation combined with installation preflight' $fixture.Package `
        'ValidatePackageOnly cannot be combined with installation or migration preflight.' -InstallationPreflight
    $fixture = New-PackageFixture
    Expect-Rejection 'reject package-only validation combined with profile migration' $fixture.Package `
        'ValidatePackageOnly cannot be combined with installation or migration preflight.' (Join-Path $fixture.Root 'never-inspected-profile')

    foreach ($kind in @('root', 'child', 'ancestor')) { Reparse-Case $kind }
    Write-Output "PASS TOTAL: $($passed.Count); SKIP TOTAL: $($skipped.Count)"
    if ($skipped.Count -gt 0) { $skipped | ForEach-Object { Write-Output "SKIPPED DETAIL: $_" } }
}
finally {
    foreach ($junction in @($junctions.ToArray())) { Remove-VerifiedJunction $junction }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($resolved), $tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'cloudscribe-install-package-tests-*') {
        throw 'Unsafe test cleanup root.'
    }
    if (Test-Path -LiteralPath $resolved) {
        Assert-NoReparseTree $resolved
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
