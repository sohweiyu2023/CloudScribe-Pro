[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$EnvelopeJson)
$ErrorActionPreference='Stop'
$e=$EnvelopeJson|ConvertFrom-Json
foreach($n in 'provider','accountId','projectId','operation','idempotencyKey','unresolvedSubmission') { if([string]::IsNullOrWhiteSpace([string]$e.$n)){ if($n -ne 'unresolvedSubmission'){throw "Missing $n"} } }
if($e.provider -ne 'google'){throw 'Persisted queue provider identity drift.'}
if([bool]$e.unresolvedSubmission -and [string]::IsNullOrWhiteSpace([string]$e.providerRequestId)){throw 'Unresolved Google submission requires provider reconciliation identity.'}
if([bool]$e.unresolvedSubmission -and [bool]$e.allowBillableSubmit){throw 'Duplicate billable submission forbidden while unresolved.'}
Write-Host 'Stage6 persisted Google queue envelope admitted.'