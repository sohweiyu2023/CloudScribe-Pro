[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$StateJson)
$ErrorActionPreference='Stop'
$s=$StateJson|ConvertFrom-Json
foreach($n in 'cacheKey','mediaMaterialized','active','pinned','referenced','unresolvedSubmission') { if($null -eq $s.$n){throw "Missing $n"} }
$protected=[bool]$s.active -or [bool]$s.pinned -or [bool]$s.referenced -or [bool]$s.unresolvedSubmission
if($protected -and -not [bool]$s.mediaMaterialized){throw 'Protection cannot reference absent cache media.'}
if([bool]$s.active -and -not [bool]$s.unresolvedSubmission -and $s.providerOutcome -eq 'Unknown'){throw 'Unknown provider outcome must remain unresolved.'}
if([string]::IsNullOrWhiteSpace([string]$s.cacheKey)){throw 'Private cache key is required.'}
Write-Host 'Stage5 cache protection state admitted.'