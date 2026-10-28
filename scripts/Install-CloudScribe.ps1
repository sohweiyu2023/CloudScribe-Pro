[CmdletBinding()]
param(
    [string]$PackageRoot=$PSScriptRoot,
    [string]$MigrateProfileFrom,
    [string]$ReviewedRecoveryPolicyPath,
    [string]$ReviewedRecoveryPolicySha256,
    [switch]$ValidateOnly,
    [switch]$ValidatePackageOnly
)
$ErrorActionPreference='Stop'
if($ValidatePackageOnly -and ($ValidateOnly -or $MigrateProfileFrom)){throw 'ValidatePackageOnly cannot be combined with installation or migration preflight.'}
if($ValidatePackageOnly -and ($ReviewedRecoveryPolicyPath -or $ReviewedRecoveryPolicySha256)){throw 'A private recovery policy is not part of a clean package.'}
if([bool]$ReviewedRecoveryPolicyPath -ne [bool]$ReviewedRecoveryPolicySha256){throw 'Recovery policy admission requires both the separately reviewed file and its independently approved SHA256.'}
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
function Assert-UniqueRecoveryJson($Element){
    if($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object){
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($property in $Element.EnumerateObject()){
            if(-not $seen.Add($property.Name)){throw 'Recovery policy has ambiguous properties.'}
            Assert-UniqueRecoveryJson $property.Value
        }
    }elseif($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array){
        foreach($item in $Element.EnumerateArray()){Assert-UniqueRecoveryJson $item}
    }
}
function Assert-RecoveryRecord($Element,[hashtable]$Fields){
    if($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object -or @($Element.EnumerateObject()).Count -ne $Fields.Count){throw 'Recovery policy record shape is invalid.'}
    foreach($property in $Element.EnumerateObject()){
        if($property.Name -cnotin @($Fields.Keys)){throw 'Recovery policy contains an unknown record field.'}
        $value=$property.Value
        switch($Fields[$property.Name]){
            'String' {if($value.ValueKind -ne [Text.Json.JsonValueKind]::String){throw 'Recovery policy string is invalid.'}}
            'Int32' {$null=$value.GetInt32()}
            'Int64' {$null=$value.GetInt64()}
            'Guid' {$null=$value.GetGuid()}
            'Date' {$null=$value.GetDateTimeOffset()}
            'Array' {if($value.ValueKind -ne [Text.Json.JsonValueKind]::Array){throw 'Recovery policy array is invalid.'}}
            'Any' {if($value.ValueKind -notin @([Text.Json.JsonValueKind]::Object,[Text.Json.JsonValueKind]::Null)){throw 'Nullable recovery record is invalid.'}}
            'Object' {if($value.ValueKind -ne [Text.Json.JsonValueKind]::Object){throw 'Recovery policy object is invalid.'}}
            default {throw 'Unknown recovery policy validation type.'}
        }
    }
}
function Write-RecoveryCanonicalRecord($Writer,$Element,[string]$Kind){
    $fields=switch($Kind){
        'Predecessor' {@('LogicalNarrationId:String','AccountId:String','OperationId:String','HistoricalKey:String','HistoricalStateSha256:String','ProjectId:String','DocumentId:Guid','RevisionId:Guid','TextSha256:String','OriginalPayloadSha256:String','AdmissionSha256:Empty','ExpiresAtUtc:Date','MaximumNewAttempts:Number','MaximumAdditionalReservationMinorUnits:Number','Successor:Successor')}
        'Successor' {@('PriorAuthorizationId:Guid','PriorChildKey:String','PriorStateSha256:String','PriorAdmissionSha256:String','FinancialStateSha256:String','SourceApprovalSha256:String','AllocationTransition:Allocation')}
        'Allocation' {@('PriorScopeSha256:String','PriorAllocationCeilingMinorUnits:Number','NewAllocationCeilingMinorUnits:Number','NewRemainingMinorUnits:Number','SourceApprovalSha256:String')}
        default {throw 'Unknown canonical recovery record.'}
    }
    $Writer.WriteStartObject()
    foreach($field in $fields){
        $name,$type=$field.Split(':',2);$value=$Element.GetProperty($name)
        switch($type){
            'String' {$Writer.WriteString([string]$name,[string]$value.GetString())}
            'Empty' {$Writer.WriteString([string]$name,[string]'')}
            'Guid' {$Writer.WriteString([string]$name,[guid]$value.GetGuid())}
            'Date' {$Writer.WriteString([string]$name,[DateTimeOffset]$value.GetDateTimeOffset())}
            'Number' {$Writer.WriteNumber([string]$name,[long]$value.GetInt64())}
            default {
                $Writer.WritePropertyName([string]$name)
                if($value.ValueKind -eq [Text.Json.JsonValueKind]::Null){$Writer.WriteNullValue()}
                else{Write-RecoveryCanonicalRecord $Writer $value $type}
            }
        }
    }
    $Writer.WriteEndObject()
}
function Get-RecoveryCanonicalGrantSha256($Element){
    $stream=[IO.MemoryStream]::new();$writer=[Text.Json.Utf8JsonWriter]::new($stream)
    try{
        Write-RecoveryCanonicalRecord $writer $Element 'Predecessor';$writer.Flush()
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream.ToArray())).ToLowerInvariant()
    }finally{$writer.Dispose();$stream.Dispose()}
}
function Assert-RecoveryNestedPolicy($Root){
    Assert-RecoveryRecord $Root @{SchemaVersion='Int32';AppSha256='String';InfrastructureSha256='String';ProfileRootSha256='String';AccountId='String';ProjectId='String';Currency='String';Scale='Int32';ExpiresAtUtc='Date';Budget='Object';HistoricalAudio='Array';Predecessors='Array'}
    if($Root.GetProperty('SchemaVersion').GetInt32() -notin @(2,3)){throw 'Recovery policy requires bounded-grant schema2 or explicit-successor schema3.'}
    if([string]::IsNullOrWhiteSpace($Root.GetProperty('AccountId').GetString()) -or [string]::IsNullOrWhiteSpace($Root.GetProperty('ProjectId').GetString()) -or $Root.GetProperty('Currency').GetString() -cnotmatch '^[A-Z]{3}$' -or $Root.GetProperty('Scale').GetInt32() -notin 0..9){throw 'Recovery policy scope is invalid.'}
    foreach($name in @('AppSha256','InfrastructureSha256','ProfileRootSha256')){if($Root.GetProperty($name).GetString() -notmatch '^[a-fA-F0-9]{64}$'){throw 'Recovery policy hash is invalid.'}}
    $budget=$Root.GetProperty('Budget')
    $budgetFields=@{ProjectCommittedMinorUnits='Int64';ProjectCeilingMinorUnits='Int64';AllocationCommittedMinorUnits='Int64';AllocationCeilingMinorUnits='Int64';ReviewedEvidenceSha256='String';QueueRows='Array';SpendRows='Array';RecoveryRows='Array'}
    if($Root.GetProperty('SchemaVersion').GetInt32() -eq 3){$budgetFields['TransitionRows']='Array'}
    Assert-RecoveryRecord $budget $budgetFields
    if($budget.GetProperty('ProjectCommittedMinorUnits').GetInt64() -lt 0 -or $budget.GetProperty('AllocationCommittedMinorUnits').GetInt64() -lt 0 -or $budget.GetProperty('ProjectCeilingMinorUnits').GetInt64() -lt $budget.GetProperty('ProjectCommittedMinorUnits').GetInt64() -or $budget.GetProperty('AllocationCeilingMinorUnits').GetInt64() -lt $budget.GetProperty('AllocationCommittedMinorUnits').GetInt64() -or $budget.GetProperty('ReviewedEvidenceSha256').GetString() -notmatch '^[a-fA-F0-9]{64}$'){throw 'Recovery policy budget bounds are invalid.'}
    $pinNames=@('QueueRows','SpendRows','RecoveryRows')
    if($Root.GetProperty('SchemaVersion').GetInt32() -eq 3){$pinNames+='TransitionRows'}
    foreach($name in $pinNames){
        $pins=$budget.GetProperty($name)
        if($pins.GetArrayLength() -gt 10000){throw 'Recovery policy row bound exceeded.'}
        $identities=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($pin in $pins.EnumerateArray()){
            Assert-RecoveryRecord $pin @{Identity='String';Sha256='String'}
            $identity=$pin.GetProperty('Identity').GetString()
            if([string]::IsNullOrWhiteSpace($identity) -or -not $identities.Add($identity) -or $pin.GetProperty('Sha256').GetString() -notmatch '^[a-fA-F0-9]{64}$'){throw 'Recovery policy financial pins are invalid.'}
        }
    }
    $historical=$Root.GetProperty('HistoricalAudio');$predecessors=$Root.GetProperty('Predecessors')
    if($historical.GetArrayLength() -gt 200 -or $predecessors.GetArrayLength() -gt 200){throw 'Recovery policy admission count exceeded.'}
    $ids=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($entry in $historical.EnumerateArray()){
        Assert-RecoveryRecord $entry @{EvidenceId='String';Anchor='Object'}
        $id=$entry.GetProperty('EvidenceId').GetString()
        if([string]::IsNullOrWhiteSpace($id) -or -not $ids.Add($id)){throw 'Recovery policy evidence identity is invalid.'}
        $anchor=$entry.GetProperty('Anchor')
        Assert-RecoveryRecord $anchor @{ParentId='Guid';ParentRevisionId='Guid';ParentSha256='String';Ordinal='Int32';DocumentId='Guid';RevisionId='Guid';TextSha256='String';AccountId='String';OperationId='String';IdempotencyKey='String';LocalAttemptId='String';PayloadSha256='String';PayloadBytes='Int32';AudioSha256='String';AudioBytes='Int64';VoiceName='String';LanguageCode='String';EvidenceSha256='String'}
        if($anchor.GetProperty('AccountId').GetString() -cne $Root.GetProperty('AccountId').GetString() -or $anchor.GetProperty('OperationId').GetString() -cne 'synthesize-speech'){throw 'Recovery policy historical scope is invalid.'}
    }
    $keys=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $grants=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($entry in $predecessors.EnumerateArray()){
        $fields=@{LogicalNarrationId='String';AccountId='String';OperationId='String';HistoricalKey='String';HistoricalStateSha256='String';ProjectId='String';DocumentId='Guid';RevisionId='Guid';TextSha256='String';OriginalPayloadSha256='String';AdmissionSha256='String';ExpiresAtUtc='Date';MaximumNewAttempts='Int32';MaximumAdditionalReservationMinorUnits='Int64'}
        if($Root.GetProperty('SchemaVersion').GetInt32() -eq 3){$fields['Successor']='Object'}
        Assert-RecoveryRecord $entry $fields
        if($Root.GetProperty('SchemaVersion').GetInt32() -eq 3){
            if($Root.GetProperty('Currency').GetString() -cne 'USD' -or $Root.GetProperty('Scale').GetInt32() -ne 2){throw 'Successor admission requires USD cents.'}
            if($entry.GetProperty('ExpiresAtUtc').GetDateTimeOffset() -gt $Root.GetProperty('ExpiresAtUtc').GetDateTimeOffset()){throw 'Successor expiry exceeds policy expiry.'}
            if($historical.GetArrayLength() -ne 0 -or $predecessors.GetArrayLength() -ne 1 -or $entry.GetProperty('MaximumNewAttempts').GetInt32() -ne 1 -or $entry.GetProperty('MaximumAdditionalReservationMinorUnits').GetInt64() -ne 3){throw 'Successor admission requires one distinct one-attempt/three-cent grant and no import authority.'}
            $successor=$entry.GetProperty('Successor')
            Assert-RecoveryRecord $successor @{PriorAuthorizationId='Guid';PriorChildKey='String';PriorStateSha256='String';PriorAdmissionSha256='String';FinancialStateSha256='String';SourceApprovalSha256='String';AllocationTransition='Any'}
            if($successor.GetProperty('PriorAuthorizationId').GetGuid() -eq [guid]::Empty -or $successor.GetProperty('PriorChildKey').GetString() -cne ('recovery-google-tts:'+$successor.GetProperty('PriorAuthorizationId').GetGuid().ToString('N')) -or $successor.GetProperty('PriorAdmissionSha256').GetString() -ceq $entry.GetProperty('AdmissionSha256').GetString()){throw 'Successor lineage identity is invalid.'}
            foreach($name in @('PriorStateSha256','PriorAdmissionSha256','FinancialStateSha256','SourceApprovalSha256')){if($successor.GetProperty($name).GetString() -cnotmatch '^[a-f0-9]{64}$'){throw 'Successor digest is invalid.'}}
            $transition=$successor.GetProperty('AllocationTransition')
            if($transition.ValueKind -ne [Text.Json.JsonValueKind]::Null){
                Assert-RecoveryRecord $transition @{PriorScopeSha256='String';PriorAllocationCeilingMinorUnits='Int64';NewAllocationCeilingMinorUnits='Int64';NewRemainingMinorUnits='Int64';SourceApprovalSha256='String'}
                if($transition.GetProperty('PriorScopeSha256').GetString() -cnotmatch '^[a-f0-9]{64}$' -or $transition.GetProperty('SourceApprovalSha256').GetString() -cne $successor.GetProperty('SourceApprovalSha256').GetString() -or $transition.GetProperty('PriorAllocationCeilingMinorUnits').GetInt64() -lt 0 -or $transition.GetProperty('NewAllocationCeilingMinorUnits').GetInt64() -le $transition.GetProperty('PriorAllocationCeilingMinorUnits').GetInt64() -or $transition.GetProperty('NewAllocationCeilingMinorUnits').GetInt64() -ne $budget.GetProperty('AllocationCeilingMinorUnits').GetInt64() -or $transition.GetProperty('NewRemainingMinorUnits').GetInt64() -ne ($budget.GetProperty('AllocationCeilingMinorUnits').GetInt64()-$budget.GetProperty('AllocationCommittedMinorUnits').GetInt64())){throw 'Allocation transition is invalid.'}
            }
        }
        if($Root.GetProperty('SchemaVersion').GetInt32() -eq 3 -and $entry.GetProperty('AdmissionSha256').GetString() -cne (Get-RecoveryCanonicalGrantSha256 $entry)){throw 'Successor grant digest does not bind its full canonical authority.'}
        if($entry.GetProperty('MaximumNewAttempts').GetInt32() -notin 1..200 -or $entry.GetProperty('MaximumAdditionalReservationMinorUnits').GetInt64() -lt 0 -or $entry.GetProperty('AdmissionSha256').GetString() -notmatch '^[a-fA-F0-9]{64}$' -or -not $grants.Add($entry.GetProperty('AdmissionSha256').GetString())){throw 'Recovery policy grant bounds or identity are invalid.'}
        if(-not $keys.Add($entry.GetProperty('HistoricalKey').GetString()) -or $entry.GetProperty('AccountId').GetString() -cne $Root.GetProperty('AccountId').GetString() -or $entry.GetProperty('ProjectId').GetString() -cne $Root.GetProperty('ProjectId').GetString() -or $entry.GetProperty('OperationId').GetString() -cne 'synthesize-speech'){throw 'Recovery policy predecessor scope is invalid.'}
    }
}
function Get-ReviewedRecoveryPolicy([string]$Profile){
    if(-not $ReviewedRecoveryPolicyPath){return $null}
    if($PSVersionTable.PSVersion.Major -lt 7 -or [Environment]::Version.Major -lt 8){throw 'Optional recovery policy admission requires PowerShell 7 on .NET 8 or later. Ordinary updates without a recovery policy remain supported in Windows PowerShell.'}
    if(-not $Profile -or $ReviewedRecoveryPolicySha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Recovery policy requires an inspected existing profile and independent hash.'}
    $path=(Resolve-Path -LiteralPath $ReviewedRecoveryPolicyPath).Path
    Assert-NoLinks $path
    $stream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($stream.Length -lt 1 -or $stream.Length -gt 1048576){throw 'Recovery policy exceeds its reviewed size bound.'}
        $bytes=[byte[]]::new([int]$stream.Length);$stream.ReadExactly($bytes)
    }finally{$stream.Dispose()}
    $sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    if($sha -ne $ReviewedRecoveryPolicySha256){throw 'Recovery policy does not match the independently approved SHA256.'}
    $options=[Text.Json.JsonDocumentOptions]::new();$options.MaxDepth=16
    $json=[Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($bytes),$options)
    try{
        Assert-UniqueRecoveryJson $json.RootElement
        $root=$json.RootElement
        Assert-RecoveryNestedPolicy $root
        $allowed=@('SchemaVersion','AppSha256','InfrastructureSha256','ProfileRootSha256','AccountId','ProjectId','Currency','Scale','ExpiresAtUtc','Budget','HistoricalAudio','Predecessors')
        if(@($root.EnumerateObject()).Count -ne $allowed.Count -or @($root.EnumerateObject() | Where-Object {$_.Name -cnotin $allowed}).Count){throw 'Recovery policy schema is incomplete or unknown.'}
        if($root.GetProperty('SchemaVersion').GetInt32() -ne 2 -or $root.GetProperty('ExpiresAtUtc').GetDateTimeOffset() -le [DateTimeOffset]::UtcNow){throw 'Recovery policy is incompatible or expired.'}
        if($root.GetProperty('InfrastructureSha256').GetString() -ne (Get-FileHash -LiteralPath (Join-Path $package 'app/CloudScribe.Infrastructure.dll')).Hash){throw 'Recovery policy belongs to another exact build.'}
        if($root.GetProperty('AppSha256').GetString() -ne (Get-FileHash -LiteralPath (Join-Path $package 'app/CloudScribe.dll')).Hash){throw 'Recovery policy belongs to another exact app build.'}
        $canonical=[IO.Path]::GetFullPath($Profile).TrimEnd([char[]]@('\','/')).ToUpperInvariant()
        $profileSha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
        if($root.GetProperty('ProfileRootSha256').GetString() -ne $profileSha){throw 'Recovery policy belongs to another profile.'}
    }finally{$json.Dispose()}
    return [pscustomobject]@{Bytes=$bytes;Sha256=$sha}
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
$reviewedRecovery=Get-ReviewedRecoveryPolicy $profile
if($ValidateOnly){[pscustomobject]@{Valid=$true;PackageFiles=@($all | Where-Object {-not $_.PSIsContainer}).Count;InstallRoot=$target;MigrationRequested=[bool]$migration;ReviewedRecoveryPolicy=[bool]$reviewedRecovery};return}
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
$installMarker=@{Product='CloudScribe Pro';Version=1;Profile=$profile;AppSha256=(Get-FileHash -LiteralPath (Join-Path $stage 'app/CloudScribe.dll')).Hash}
if($reviewedRecovery){
    [IO.File]::WriteAllBytes((Join-Path $stage 'recovery-admission-policy.json'),$reviewedRecovery.Bytes)
    $installMarker.RecoveryPolicySha256=$reviewedRecovery.Sha256
}
# An ordinary update does not copy or silently re-admit any previous private recovery authority.
$installMarker | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'cloudscribe-install.json') -Encoding utf8
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
