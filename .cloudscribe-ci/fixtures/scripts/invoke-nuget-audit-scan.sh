#!/usr/bin/env bash
set -euo pipefail

if (( $# != 1 )); then
  echo "Usage: scripts/invoke-nuget-audit-scan.sh <project.csproj>" >&2
  exit 64
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
project="$1"
if [[ "$project" = /* ]]; then
  project_path="$project"
else
  project_path="$repo_root/$project"
fi

if [[ "$project_path" != *.csproj || ! -f "$project_path" ]]; then
  echo "Audit target must be an existing project beneath the CloudScribe repository: $project" >&2
  exit 64
fi
project_path="$(realpath -- "$project_path")"
if [[ "$project_path" != "$repo_root/"* || "$project_path" != *.csproj ]]; then
  echo "Audit target must resolve beneath the CloudScribe repository: $project" >&2
  exit 64
fi

cd "$repo_root"
maximum_attempts=3
for (( attempt = 1; attempt <= maximum_attempts; attempt++ )); do
  set +e
  restore_output="$(dotnet restore "$project_path" --locked-mode --disable-parallel --configfile NuGet.config \
    --force --no-http-cache -p:CloudScribeNuGetAuditPipeline=true 2>&1)"
  restore_status=$?
  set -e
  printf '%s\n' "$restore_output" >&2

  if (( restore_status == 0 )); then
    break
  fi
  if (( attempt == maximum_attempts )) || ! grep -Eq '(^|[^[:alnum:]])(NU1900|NU1301)([^[:alnum:]]|$)' <<<"$restore_output"; then
    exit "$restore_status"
  fi

  delay_seconds=$(( attempt == 1 ? 2 : 5 ))
  printf 'Strict NuGet audit restore hit transient source failure NU1900/NU1301; retrying attempt %d/%d in %d seconds.\n' \
    "$((attempt + 1))" "$maximum_attempts" "$delay_seconds" >&2
  sleep "$delay_seconds"
done

dotnet package list --project "$project_path" --vulnerable --include-transitive --no-restore \
  --format json --output-version 1
