#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
unset PLATFORM || true
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export AVALONIA_TELEMETRY_OPTOUT=1
export PYTHONDONTWRITEBYTECODE=1
export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0

cleanup_generated() {
  find . -type d \( -name bin -o -name obj -o -name TestResults -o -name __pycache__ -o -name .vs \) -prune -exec rm -rf {} +
}
trap cleanup_generated EXIT

run_logged() {
  local log_file="$1"
  shift
  local stderr_file="${log_file%.log}.stderr.log"
  mkdir -p "$(dirname "$log_file")"
  python3 tools/run_bounded_process.py \
    --timeout-seconds 1200 \
    --max-output-bytes 67108864 \
    --stdout-file "$log_file" \
    --stderr-file "$stderr_file" \
    --tee -- "$@"
}

run_quiet_bounded() {
  python3 tools/run_bounded_process.py \
    --timeout-seconds 120 \
    --max-output-bytes 8388608 -- "$@"
}

if ! command -v dotnet >/dev/null 2>&1; then
  echo "The pinned .NET SDK is required for Stage 2 promotion; dotnet was not found." >&2
  exit 3
fi
if [[ "$(uname -s)" != "Linux" ]]; then
  echo "This promotion verifier requires Linux/Xvfb runtime evidence. Use scripts/verify-stage2.ps1 on Windows." >&2
  exit 4
fi
for command_name in xvfb-run timeout python3 tee; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Stage 2 promotion requires '$command_name' so runtime or execution evidence cannot be silently skipped." >&2
    exit 4
  fi
done

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
evidence_root="${CLOUDSCRIBE_STAGE2_EVIDENCE_DIR:-$(dirname "$repo_root")/CloudScribe_Stage2_Runtime_Evidence_${stamp}}"
evidence_root="$(python3 tools/prepare_physical_directory.py "$evidence_root" \
  --label "Stage 2 evidence directory" --forbid-root "$repo_root" --require-empty)"
for child in logs test-results package-scans visual; do
  python3 tools/prepare_physical_directory.py "$evidence_root/$child" \
    --label "Stage 2 evidence child directory" --forbid-root "$repo_root" >/dev/null
done

required_sdk="$(python3 -c 'import json; print(json.load(open("global.json"))["sdk"]["version"])')"
run_logged "$evidence_root/logs/sdk-version.log" dotnet --version
actual_sdk="$(tr -d '\r\n' < "$evidence_root/logs/sdk-version.log")"
run_logged "$evidence_root/logs/msbuild-version.log" dotnet msbuild -version -nologo
actual_msbuild="$(awk 'NF { line=$0 } END { print line }' "$evidence_root/logs/msbuild-version.log")"
run_logged "$evidence_root/logs/sdk-policy.log" \
  python3 tools/verify_dotnet_sdk_version.py --required "$required_sdk" --actual "$actual_sdk" --msbuild "$actual_msbuild"

all_projects=(
  src/CloudScribe.App/CloudScribe.App.csproj
  src/CloudScribe.Application/CloudScribe.Application.csproj
  src/CloudScribe.Domain/CloudScribe.Domain.csproj
  src/CloudScribe.Infrastructure/CloudScribe.Infrastructure.csproj
  src/CloudScribe.Providers.Abstractions/CloudScribe.Providers.Abstractions.csproj
  tests/CloudScribe.Domain.Tests/CloudScribe.Domain.Tests.csproj
  tests/CloudScribe.Application.Tests/CloudScribe.Application.Tests.csproj
  tests/CloudScribe.Infrastructure.Tests/CloudScribe.Infrastructure.Tests.csproj
  tests/CloudScribe.Architecture.Tests/CloudScribe.Architecture.Tests.csproj
)
build_projects=(
  src/CloudScribe.Domain/CloudScribe.Domain.csproj
  src/CloudScribe.Providers.Abstractions/CloudScribe.Providers.Abstractions.csproj
  src/CloudScribe.Application/CloudScribe.Application.csproj
  src/CloudScribe.Infrastructure/CloudScribe.Infrastructure.csproj
  src/CloudScribe.App/CloudScribe.App.csproj
  tests/CloudScribe.Domain.Tests/CloudScribe.Domain.Tests.csproj
  tests/CloudScribe.Application.Tests/CloudScribe.Application.Tests.csproj
  tests/CloudScribe.Infrastructure.Tests/CloudScribe.Infrastructure.Tests.csproj
  tests/CloudScribe.Architecture.Tests/CloudScribe.Architecture.Tests.csproj
)
test_projects=(
  tests/CloudScribe.Domain.Tests/CloudScribe.Domain.Tests.csproj
  tests/CloudScribe.Application.Tests/CloudScribe.Application.Tests.csproj
  tests/CloudScribe.Infrastructure.Tests/CloudScribe.Infrastructure.Tests.csproj
  tests/CloudScribe.Architecture.Tests/CloudScribe.Architecture.Tests.csproj
)

cleanup_generated
run_logged "$evidence_root/logs/01-stage1-source.log" python3 tools/verify_stage1_source.py
run_logged "$evidence_root/logs/02-stage2-source.log" python3 tools/verify_stage2_source.py
run_logged "$evidence_root/logs/03-project-dependencies.log" python3 tools/verify_project_dependencies.py
run_logged "$evidence_root/logs/03-source-manifest-preflight.log" python3 tools/update_sha256_manifest.py --check
source_manifest_sha256="$(sha256sum SHA256SUMS.txt | awk '{print $1}')"
repository_version="$(python3 -c 'import json; print(json.load(open("SESSION_STATE.json"))["repository_version"])')"
run_logged "$evidence_root/logs/04-dotnet-info.log" dotnet --info

for index in "${!all_projects[@]}"; do
  project="${all_projects[$index]}"
  run_quiet_bounded dotnet build-server shutdown || true
  run_logged "$evidence_root/logs/restore-${index}.log" \
    dotnet restore "$project" --locked-mode --disable-parallel --configfile NuGet.config
  run_quiet_bounded dotnet build-server shutdown || true
done

for configuration in Debug Release; do
  for index in "${!build_projects[@]}"; do
    project="${build_projects[$index]}"
    run_logged "$evidence_root/logs/build-${configuration}-${index}.log" \
      dotnet build "$project" -c "$configuration" --no-restore --disable-build-servers -m:1 -nodeReuse:false \
        -p:BuildProjectReferences=false -p:BuildInParallel=false -p:UseSharedCompilation=false
  done
done

for index in "${!test_projects[@]}"; do
  project="${test_projects[$index]}"
  result_dir="$evidence_root/test-results/$index"
  mkdir -p "$result_dir"
  run_logged "$evidence_root/logs/test-${index}.log" \
    dotnet test "$project" -c Release --no-build --no-restore -m:1 -nodeReuse:false \
      -p:UseSharedCompilation=false --results-directory "$result_dir" \
      --logger "trx;LogFileName=stage2-tests.trx"
done

run_logged "$evidence_root/logs/format.log" \
  dotnet format CloudScribe.sln --verify-no-changes --no-restore

for index in "${!all_projects[@]}"; do
  project="${all_projects[$index]}"
  python3 tools/run_bounded_process.py --timeout-seconds 300 --max-output-bytes 5242880 \
    --stdout-file "$evidence_root/package-scans/${index}-vulnerable.json" \
    --stderr-file "$evidence_root/logs/package-${index}-vulnerable.stderr.log" -- \
    scripts/invoke-nuget-audit-scan.sh "$project"
  python3 tools/run_bounded_process.py --timeout-seconds 300 --max-output-bytes 5242880 \
    --stdout-file "$evidence_root/package-scans/${index}-deprecated.json" \
    --stderr-file "$evidence_root/logs/package-${index}-deprecated.stderr.log" -- \
    dotnet package list --project "$project" --deprecated --include-transitive --no-restore --format json --output-version 1
done
run_logged "$evidence_root/logs/package-scan-validation.log" \
  python3 tools/verify_dotnet_package_scan.py "$evidence_root/package-scans"

run_logged "$evidence_root/logs/stage1-runtime-smoke.log" scripts/smoke-stage1-linux.sh
run_logged "$evidence_root/logs/stage2-visual-capture.log" scripts/capture-stage2-linux.sh "$evidence_root/visual"

cleanup_generated
run_logged "$evidence_root/logs/sha256-manifest.log" python3 tools/update_sha256_manifest.py --check
run_logged "$evidence_root/logs/stage1-source-final.log" python3 tools/verify_stage1_source.py
run_logged "$evidence_root/logs/stage2-source-final.log" python3 tools/verify_stage2_source.py
run_logged "$evidence_root/logs/repository-governance.log" python3 tools/verify_repository.py
run_logged "$evidence_root/logs/python-regression-inventory.log" \
  python3 tools/run_python_regression_shards.py --all

run_logged "$evidence_root/logs/stage2-evidence-inventory.log" \
  python3 tools/verify_stage2_evidence_inventory.py "$evidence_root"
final_source_manifest_sha256="$(sha256sum SHA256SUMS.txt | awk '{print $1}')"
final_repository_version="$(python3 -c 'import json; print(json.load(open("SESSION_STATE.json"))["repository_version"])')"
[[ "$final_source_manifest_sha256" == "$source_manifest_sha256" ]] || { echo "Source manifest changed during Stage 2 verification." >&2; exit 1; }
[[ "$final_repository_version" == "$repository_version" ]] || { echo "Repository version changed during Stage 2 verification." >&2; exit 1; }
package_scan_count=18
screenshot_count=17
test_result_count=4

EVIDENCE_ROOT="$evidence_root" ACTUAL_SDK="$actual_sdk" SOURCE_MANIFEST_SHA256="$source_manifest_sha256" REPOSITORY_VERSION="$repository_version" python3 - <<'PY'
import datetime as dt
import json
import os
from pathlib import Path
root = Path(os.environ['EVIDENCE_ROOT'])
summary = {
    'schema': 'cloudscribe-stage2-verification-summary-1.0',
    'completed_at_utc': dt.datetime.now(dt.timezone.utc).isoformat().replace('+00:00', 'Z'),
    'status': 'passed',
    'platform': 'Linux/Xvfb',
    'dotnet_sdk': os.environ['ACTUAL_SDK'],
    'repository_version': os.environ['REPOSITORY_VERSION'],
    'source_manifest_sha256': os.environ['SOURCE_MANIFEST_SHA256'],
    'evidence_retained': True,
    'package_scan_files': len(list((root / 'package-scans').glob('*.json'))),
    'screenshot_files': len(list((root / 'visual').glob('*.png'))),
    'test_result_files': len(list((root / 'test-results').rglob('*.trx'))),
}
(root / 'verification-summary.json').write_text(json.dumps(summary, indent=2) + '\n', encoding='utf-8')
PY
printf 'Stage 2 verification evidence retained at: %s\n' "$evidence_root"
