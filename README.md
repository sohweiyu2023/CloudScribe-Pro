# CloudScribe Pro

CloudScribe Pro is a cross-platform desktop app (Avalonia + .NET 10) that reproduces core NaturalReader workflows while using Google Cloud Text-to-Speech as the synthesis engine.

## Architecture Overview

- **UI (`CloudReader.App`)**: Avalonia Fluent UI with strict MVVM (CommunityToolkit.Mvvm). Three-pane layout: library/sidebar, editor center, voice+generation controls on right.
- **Domain (`CloudReader.Core`)**: chunking, preprocessing, script parsing hooks, cost estimator, adaptive rate limiter.
- **Persistence (`CloudReader.Infrastructure`)**: EF Core + SQLite entities and db context, migrations scaffold.
- **Google Integration (`CloudReader.GoogleTts`)**: official Google client adapters:
  - `Google.Cloud.TextToSpeech.V1` for voices list and standard synth
  - `Google.Cloud.TextToSpeech.V1Beta1` for long audio synth operations
- **Audio (`CloudReader.Audio`)**: ffmpeg auto-download and deterministic merge pipeline via Xabe.FFmpeg.
- **Tests (`CloudReader.Tests`)**: unit tests for UTF-8 byte chunking, sanitizer behavior, and cost model math.

## Folder Structure

```text
src/
  CloudReader.App/
  CloudReader.Core/
  CloudReader.Infrastructure/
  CloudReader.GoogleTts/
  CloudReader.Audio/
tests/
  CloudReader.Tests/
terraform/
scripts/
.github/workflows/
```

## EF Core Entities + Migration Plan

Implemented schema classes in `src/CloudReader.Infrastructure/Entities/Entities.cs`:
- Documents
- Tags
- DocumentTags
- VoicePresets
- Generations
- Segments
- Lexicon
- MonthlyUsage

`CloudReaderDbContext` configures join keys and monthly usage uniqueness.

Planned migration flow:
1. `InitialCreate` migration creates all required tables and indexes.
2. `QueueAndCacheEnhancements` adds queue-state/cache indexes.
3. `VersioningAndBookmarks` adds generation version metadata and bookmarks.

> Prerequisite: install the .NET 10 SDK before building (Ubuntu: `sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0`).
> If EF tooling is unavailable in a constrained environment, a SQL migration script placeholder is checked in under `src/CloudReader.Infrastructure/Migrations/0001_initial.sql`.

## Implemented MVP Steps

### 1) Voice catalog/filtering/preview foundation
- `GoogleTtsClient.ListVoicesAsync` maps all voices from `voices.list` and infers tier by name conventions.
- `SynthesizeAsync` supports one-click preview generation inputs.

### 2) Plain text + Mode A chunked synthesis foundation
- `Utf8Chunker` enforces UTF-8 byte-aware chunking and sentence-friendly boundaries.
- Supports fallback splitting at whitespace and rune boundaries.

### 3) Library persistence foundation
- EF Core entities and DbContext for required schema.

### 4) Queue/rate limiter foundation
- `AdaptiveRateLimiter` lowers concurrency when throttling signals occur.
- Polly retry pipeline for transient Google RPC statuses (429/resource exhausted + unavailable).

### 5) Cost meter foundation
- `CostEstimator` tracks free-tier and paid-tier deltas by characters, not bytes.

### 6) Script mode foundation
- Document mode enum includes PlainText and ConversationScript.
- Data model supports per-segment speaker and preset references.

### 7) Mode B long audio synthesis
- `StartLongAudioAsync` submits long-running synthesis to GCS output URI.
- `PollLongAudioCompleteAsync` checks operation completion.

## Google Cloud setup

1. Enable billing in your Google Cloud project.
2. Enable APIs:
   - Text-to-Speech API
   - Cloud Storage API
3. Auth options:
   - ADC (`gcloud auth application-default login`)
   - Service Account JSON selected in app (encrypt at rest in production implementation)

## Terraform (Mode B provisioning)

```bash
cd terraform
terraform init
terraform apply -var="project_id=YOUR_PROJECT" -var="region=us-central1" -var="bucket_name=your-bucket"
```

Outputs:
- bucket name
- service account email
- credentials environment-variable template

## FFmpeg behavior

On first launch, ffmpeg is downloaded to app data under `ffmpeg/` with `Xabe.FFmpeg.Downloader` and path is reused for merge jobs.

## Troubleshooting

- **5,000-byte errors**: lower chunk max (default headroom 4,500 bytes).
- **429/RESOURCE_EXHAUSTED**: reduce concurrency and retry with backoff.
- **Mode B failures**: validate GCS bucket IAM for object create/view and API enablement.
- **Auth failures**: verify ADC or active service-account key path.

## UI Style Guide (Fluent / Win11)

- Theme: Avalonia FluentTheme with light/dark support.
- Typography: Segoe UI (Windows) fallback defaults elsewhere.
- Spacing scale: 4/8/12/16/24.
- Navigation: left library rail, top command bar, right generation inspector.
- Interaction: subtle hover/press via Fluent defaults; keep density moderate.

## Build & release

Scripts in `scripts/`:
- `publish-win-x64.ps1`
- `publish-linux-x64.sh`
- `publish-osx-arm64.sh`

CI workflow in `.github/workflows/build.yml` restores/builds/tests and publishes artifacts.
