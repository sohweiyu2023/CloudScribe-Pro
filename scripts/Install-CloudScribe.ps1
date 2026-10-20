[CmdletBinding()]
param(
    [string]$PackageRoot=$PSScriptRoot,
    [string]$MigrateProfileFrom,
    [switch]$ValidateOnly
)
$ErrorActionPreference='Stop'
$package=(Resolve-Path -LiteralPath $PackageRoot).Path
$programs=[IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))
$target=Join-Path $programs 'CloudScribe Pro'
if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($target)) -ne $programs){throw 'Install root escaped the Programs directory.'}
function Assert-NoLinks([string]$Path){
    $current=[IO.Path]::GetFullPath($Path)
    while($current){
        if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Linked install/profile path is not allowed: $current"}
        $current=Split-Path -Parent $current
    }
}
foreach($required in @('app/CloudScribe.exe','app/CloudScribe.dll','app/appsettings.json','runtime/dotnet.exe','runtime/shared/Microsoft.NETCore.App')){
    if(-not (Test-Path -LiteralPath (Join-Path $package $required))){throw "Incomplete clean package: $required"}
}
Assert-NoLinks $package
$all=@(Get-ChildItem -LiteralPath $package -Recurse -Force)
if($all | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw 'Package contains links.'}
if($all | Where-Object {$_.Extension -in @('.db','.sqlite','.mp3','.wav','.flac','.utf8','.pdb','.jsonl','.log','.previous','.key','.pem','.pfx')}){throw 'Package contains private, diagnostic or debug files; use a clean package.'}
foreach($file in $all | Where-Object {-not $_.PSIsContainer}){
    $relative=$file.FullName.Substring($package.Length+1).Replace('\','/')
    if($relative -notmatch '^(app|runtime)/' -and $relative -notmatch '^[^/]+\.(md|txt|cmd)$|^INSTALL-CLOUDSCRIBE\.ps1$'){throw "Unexpected package entry: $relative"}
    if($file.Extension -eq '.json' -and $file.Name -notin @('appsettings.json','CloudScribe.deps.json','CloudScribe.runtimeconfig.json','Microsoft.NETCore.App.deps.json','Microsoft.NETCore.App.runtimeconfig.json')){throw 'Unexpected JSON file; never install a credential-bearing package.'}
}
$settings=Get-Content -LiteralPath (Join-Path $package 'app/appsettings.json') -Raw | ConvertFrom-Json
if($settings.CloudScribe.AppDataDirectoryOverride){throw 'The source package must not contain a personal profile override.'}
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
$menu=[Environment]::GetFolderPath('Programs')
$shortcut=Join-Path $menu 'CloudScribe Pro.lnk'
$shell=New-Object -ComObject WScript.Shell
$link=$shell.CreateShortcut($shortcut)
$link.TargetPath=Join-Path $target 'app/CloudScribe.exe'
$link.WorkingDirectory=Join-Path $target 'app'
$link.Arguments=''
$link.IconLocation=$link.TargetPath+',0'
$link.Description='CloudScribe Pro - text to MP3'
$link.Save()
[pscustomobject]@{Executable=$link.TargetPath;Shortcut=$shortcut;Profile=$profile;PreviousInstall=$(if($hadPrevious){$backup}else{$null});Taskbar='Right-click the running CloudScribe gold-star icon and choose Pin to taskbar. Windows controls pinning.'}
