[CmdletBinding()]
param(
    [string]$PackageRoot=$PSScriptRoot,
    [string]$MigrateProfileFrom,
    [switch]$ValidateOnly,
    [switch]$ValidatePackageOnly
)
$ErrorActionPreference='Stop'
if($ValidatePackageOnly -and ($ValidateOnly -or $MigrateProfileFrom)){throw 'ValidatePackageOnly cannot be combined with installation or migration preflight.'}
$package=[IO.Path]::GetFullPath((Resolve-Path -LiteralPath $PackageRoot).Path)
if($package.Length -gt [IO.Path]::GetPathRoot($package).Length){$package=$package.TrimEnd([char[]]@('\','/'))}
$packagePrefix=$package.TrimEnd([char[]]@('\','/'))+[IO.Path]::DirectorySeparatorChar
function Assert-NoLinks([string]$Path){
    $current=[IO.Path]::GetFullPath($Path)
    while($current){
        if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Linked install/profile path is not allowed: $current"}
        $current=Split-Path -Parent $current
    }
}
function Get-AllowedNoticeFiles {
    $inventoryPath=Join-Path $package 'licenses/third-party/components.json'
    # Refresh this pin only alongside the reviewed source inventory and its fixture tests.
    # The package itself cannot expand the installer's notice-file allowlist.
    $inventorySha256='d3f95b877d4ee8230a7121cd8126ebd1321340d57e00ae2aee058f1c0e2089d7'
    if((Get-FileHash -LiteralPath $inventoryPath -Algorithm SHA256).Hash -ne $inventorySha256){throw 'Unexpected third-party inventory identity.'}
    $inventory=Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    if($inventory.schema_version -ne 1 -or @($inventory.components).Count -eq 0){throw 'Invalid third-party inventory.'}
    $allowed=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($component in $inventory.components){
        if(@($component.notice_files).Count -eq 0){throw 'Missing third-party notice records.'}
        foreach($notice in $component.notice_files){
            $relative=[string]$notice.path
            $segments=$relative.Split('/')
            if($relative -notmatch '^(texts|upstream)/' -or $relative -match '[\\:\x00-\x1f*?"<>|]' -or
                ($segments | Where-Object {$_ -in @('','.','..') -or $_.EndsWith('.') -or $_.EndsWith(' ')})){
                throw 'Unsafe third-party notice path.'
            }
            if(([IO.Path]::GetExtension($relative) -notin @('','.txt','.md') -and $relative -cne 'upstream/tmds-Tmds.DBus-COPYING') -or $notice.sha256 -notmatch '^[a-fA-F0-9]{64}$'){
                throw 'Unexpected third-party notice type or hash.'
            }
            $entry='licenses/third-party/'+$relative
            $path=[IO.Path]::GetFullPath((Join-Path $package $entry))
            if(-not $path.StartsWith($packagePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Third-party notice path escapes the package.'}
            if($allowed.ContainsKey($entry) -and $allowed[$entry] -ne $notice.sha256){throw 'Conflicting third-party notice hashes.'}
            if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw "Missing third-party notice: $entry"}
            if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $notice.sha256){throw "Changed third-party notice: $entry"}
            $allowed[$entry]=$notice.sha256
        }
    }
    return ,$allowed
}
foreach($required in @('app/CloudScribe.exe','app/CloudScribe.dll','app/appsettings.json','runtime/dotnet.exe','LICENSE','THIRD-PARTY-NOTICES.md','licenses/third-party/components.json')){
    if(-not (Test-Path -LiteralPath (Join-Path $package $required) -PathType Leaf)){throw "Incomplete clean package: $required"}
}
if(-not (Test-Path -LiteralPath (Join-Path $package 'runtime/shared/Microsoft.NETCore.App') -PathType Container)){throw 'Incomplete clean package: runtime/shared/Microsoft.NETCore.App'}
Assert-NoLinks $package
$all=@(Get-ChildItem -LiteralPath $package -Recurse -Force)
if($all | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw 'Package contains links.'}
if($all | Where-Object {$_.Extension -in @('.db','.sqlite','.mp3','.wav','.flac','.utf8','.pdb','.jsonl','.log','.previous','.key','.pem','.pfx')}){throw 'Package contains private, diagnostic or debug files; use a clean package.'}
if((Get-FileHash -LiteralPath (Join-Path $package 'LICENSE') -Algorithm SHA256).Hash -ne 'a58a65454f49c01d25bed44828fe5e678592bbdfe20353c1f7dc7cc90c6cf1d3'){throw 'Unexpected project license identity.'}
$allowedNotices=Get-AllowedNoticeFiles
foreach($file in $all | Where-Object {-not $_.PSIsContainer}){
    if(-not $file.FullName.StartsWith($packagePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Package entry escapes its root.'}
    $relative=$file.FullName.Substring($packagePrefix.Length).Replace('\','/')
    $isNoticeInventory=$relative -eq 'licenses/third-party/components.json'
    if($relative -notmatch '^(app|runtime)/' -and $relative -notmatch '^[^/]+\.(md|txt|cmd)$|^INSTALL-CLOUDSCRIBE\.ps1$' -and
        $relative -ne 'LICENSE' -and -not $isNoticeInventory -and -not $allowedNotices.ContainsKey($relative)){throw "Unexpected package entry: $relative"}
    if($file.Extension -eq '.json' -and -not $isNoticeInventory -and $file.Name -notin @('appsettings.json','CloudScribe.deps.json','CloudScribe.runtimeconfig.json','Microsoft.NETCore.App.deps.json','Microsoft.NETCore.App.runtimeconfig.json')){throw 'Unexpected JSON file; never install a credential-bearing package.'}
}
$settings=Get-Content -LiteralPath (Join-Path $package 'app/appsettings.json') -Raw | ConvertFrom-Json
if($settings.CloudScribe.AppDataDirectoryOverride){throw 'The source package must not contain a personal profile override.'}
if($ValidatePackageOnly){[pscustomobject]@{Valid=$true;PackageFiles=@($all | Where-Object {-not $_.PSIsContainer}).Count;NoticeFiles=$allowedNotices.Count;PackageOnly=$true};return}
$programs=[IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))
$target=Join-Path $programs 'CloudScribe Pro'
if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($target)) -ne $programs){throw 'Install root escaped the Programs directory.'}
$menu=[Environment]::GetFolderPath('Programs')
$shortcut=Join-Path $menu 'CloudScribe Pro.lnk'
$shell=New-Object -ComObject WScript.Shell
if(Test-Path -LiteralPath $shortcut){
    $existing=$shell.CreateShortcut($shortcut)
    $expected=Join-Path $target 'app/CloudScribe.exe'
    if(-not [string]::Equals($existing.TargetPath,$expected,[StringComparison]::OrdinalIgnoreCase) -or $existing.Arguments){
        throw 'An existing CloudScribe Pro Start Menu shortcut points elsewhere or has custom arguments. It was preserved; inspect and rename it before installing.'
    }
}
Assert-NoLinks $target
$marker=Join-Path $target 'cloudscribe-install.json'
$profile=$null
if(Test-Path -LiteralPath $target){
    if(-not (Test-Path -LiteralPath $marker)){throw 'Existing installation is not managed by this installer; inspect it before replacing anything.'}
    $identity=Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if($identity.Product -ne 'CloudScribe Pro'){throw 'Install identity mismatch.'}
    $oldSettings=Get-Content -LiteralPath (Join-Path $target 'app/appsettings.json') -Raw | ConvertFrom-Json
    $profile=$oldSettings.CloudScribe.AppDataDirectoryOverride
}
$migration=$null
if($MigrateProfileFrom){
    if($profile){throw 'A profile is already configured. Omit MigrateProfileFrom when updating.'}
    $migration=(Resolve-Path -LiteralPath $MigrateProfileFrom).Path
    Assert-NoLinks $migration
    if(-not (Test-Path -LiteralPath (Join-Path $migration 'data/cloudscribe.db'))){throw 'The migration source has no CloudScribe database.'}
    if(Get-ChildItem -LiteralPath $migration -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw 'Migration source contains links.'}
    $profile=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CloudScribe Pro/Profiles/Studio'
    Assert-NoLinks $profile
    if(Test-Path -LiteralPath $profile){throw 'Migration destination already exists; no user data will be overwritten.'}
}
if($ValidateOnly){[pscustomobject]@{Valid=$true;PackageFiles=@($all | Where-Object {-not $_.PSIsContainer}).Count;InstallRoot=$target;MigrationRequested=[bool]$migration};return}
if(Get-Process CloudScribe -ErrorAction SilentlyContinue){throw 'Close every CloudScribe window normally before installing/updating. No process will be killed.'}
[IO.Directory]::CreateDirectory($programs) | Out-Null
$suffix=[Guid]::NewGuid().ToString('N')
$stage=Join-Path $programs ('CloudScribe Pro.stage-'+$suffix)
$backup=Join-Path $programs ('CloudScribe Pro.previous-'+$suffix)
foreach($path in @($stage,$backup)){
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ne $programs){throw 'Install staging escaped the Programs directory.'}
    Assert-NoLinks $path
}
Copy-Item -LiteralPath $package -Destination $stage -Recurse
if($migration){
    [IO.Directory]::CreateDirectory((Split-Path -Parent $profile)) | Out-Null
    Copy-Item -LiteralPath $migration -Destination $profile -Recurse
    foreach($file in Get-ChildItem -LiteralPath $migration -Recurse -File){
        $copy=Join-Path $profile $file.FullName.Substring($migration.Length+1)
        if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $copy).Hash){throw 'Profile migration verification failed; original profile is intact. Installation has not switched.'}
    }
}
if($profile){
    $settings.CloudScribe.AppDataDirectoryOverride=$profile
    $settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $stage 'app/appsettings.json') -Encoding utf8
}
@{Product='CloudScribe Pro';Version=1;Profile=$profile;AppSha256=(Get-FileHash -LiteralPath (Join-Path $stage 'app/CloudScribe.dll')).Hash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'cloudscribe-install.json') -Encoding utf8
$hadPrevious=Test-Path -LiteralPath $target
if($hadPrevious){Move-Item -LiteralPath $target -Destination $backup}
try{Move-Item -LiteralPath $stage -Destination $target}catch{if($hadPrevious -and -not (Test-Path -LiteralPath $target)){Move-Item -LiteralPath $backup -Destination $target};throw}
$link=$shell.CreateShortcut($shortcut)
$link.TargetPath=Join-Path $target 'app/CloudScribe.exe'
$link.WorkingDirectory=Join-Path $target 'app'
$link.Arguments=''
$link.IconLocation=$link.TargetPath+',0'
$link.Description='CloudScribe Pro - text to MP3'
$link.Save()
[pscustomobject]@{Executable=$link.TargetPath;Shortcut=$shortcut;Profile=$profile;PreviousInstall=$(if($hadPrevious){$backup}else{$null});Taskbar='Right-click the running CloudScribe gold-star icon and choose Pin to taskbar. Windows controls pinning.'}
