#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
for command_name in xvfb-run dotnet python3 sha256sum; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Stage 2 Linux screenshot capture requires '$command_name'." >&2
    exit 4
  fi
done

if [[ $# -gt 1 ]]; then
  echo "Usage: $0 [empty-output-directory]" >&2
  exit 2
fi
if [[ $# -eq 1 ]]; then
  output_dir="$1"
else
  output_dir="${TMPDIR:-/tmp}/cloudscribe-stage2-runtime-screenshots.$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
fi
output_dir="$(python3 tools/prepare_physical_directory.py "$output_dir" \
  --label "Stage 2 screenshot output directory" --forbid-root "$repo_root" --require-empty)"

python3 tools/update_sha256_manifest.py --check
export CLOUDSCRIBE_SOURCE_MANIFEST_SHA256="$(sha256sum SHA256SUMS.txt | awk '{print $1}')"
export CLOUDSCRIBE_STAGE2_CAPTURE_MODE=1
export CLOUDSCRIBE_STAGE2_CAPTURE_DIR="$output_dir"
data_root_candidate="${TMPDIR:-/tmp}/cloudscribe-stage2-data.$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
data_root="$(python3 tools/prepare_physical_directory.py "$data_root_candidate" \
  --label "Stage 2 temporary data directory" --forbid-root "$repo_root" --require-empty)"
export CLOUDSCRIBE_CloudScribe__AppDataDirectoryOverride="$data_root/appdata"
cleanup() {
  rm -rf -- "$data_root"
}
trap cleanup EXIT

python3 tools/run_bounded_process.py \
  --timeout-seconds 60 \
  --max-output-bytes 8388608 \
  --stdout-file "$output_dir/application.stdout.log" \
  --stderr-file "$output_dir/application.stderr.log" \
  -- xvfb-run -a -s '-screen 0 1920x1200x24' \
    dotnet run --project src/CloudScribe.App/CloudScribe.App.csproj -c Release --no-build --no-restore
[[ -f "$output_dir/visual-evidence-manifest.json" ]]
[[ ! -f "$output_dir/capture-error.txt" ]]
python3 tools/verify_stage2_visual_evidence.py "$output_dir"
printf 'Stage 2 runtime screenshot evidence retained at: %s\n' "$output_dir"
