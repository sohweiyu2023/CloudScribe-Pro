[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SelectionJson,[Parameter(Mandatory=$true)][string]$RequestJson)
$ErrorActionPreference='Stop'
$s=$SelectionJson|ConvertFrom-Json;$r=$RequestJson|ConvertFrom-Json
foreach($n in 'provider','accountId','projectId','voiceId','voiceFingerprint','capabilityEvidenceId'){if([string]::IsNullOrWhiteSpace([string]$s.$n)){throw "Trusted selection missing $n"}}
foreach($n in 'provider','accountId','projectId','voiceId','voiceFingerprint'){if([string]$s.$n -cne [string]$r.$n){throw "Voice Lab request identity drift: $n"}}
if(-not [bool]$s.currentCapabilityEvidence){throw 'Stale Voice Lab capability evidence.'}
if(-not [bool]$s.authorized){throw 'Voice Lab selection is not currently authorized.'}
Write-Host 'Stage7 Voice Lab bound request admitted.'