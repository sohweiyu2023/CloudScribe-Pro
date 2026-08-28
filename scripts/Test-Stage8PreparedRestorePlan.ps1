[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PlanJson)
$ErrorActionPreference='Stop'
$p=$PlanJson|ConvertFrom-Json
if(-not $p.manifestAuthenticated){throw 'Restore manifest is not authenticated.'}
if(-not $p.archiveAdmitted){throw 'Restore archive was not admitted.'}
if(-not $p.stagedContentVerified){throw 'Staged restore content was not verified.'}
if($null -eq $p.steps -or $p.steps.Count -lt 1){throw 'Restore plan is empty.'}
$seen=@{}
foreach($s in $p.steps){
  if([string]::IsNullOrWhiteSpace([string]$s.relativePath)){throw 'Restore step path missing.'}
  $path=([string]$s.relativePath).Replace('\','/')
  if($path.StartsWith('/') -or $path.Split('/') -contains '..'){throw 'Restore target traversal rejected.'}
  $key=$path.ToLowerInvariant();if($seen.ContainsKey($key)){throw 'Duplicate/case-colliding restore target.'};$seen[$key]=$true
  if([long]$s.length -lt 0 -or [string]$s.sha256 -notmatch '^[0-9a-fA-F]{64}$'){throw 'Restore step content binding invalid.'}
}
Write-Host 'Stage8 prepared restore plan admitted.'