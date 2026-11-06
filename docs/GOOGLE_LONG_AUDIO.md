# Google Long Audio: saved-document workflow

Long Audio is an optional, separately configured path for an explicitly admitted saved document and revision. The one-shot mode submits the exact document in one asynchronous synthesis request, resumes the saved operation and downloads its private Cloud Storage output without splitting or re-encoding it. The separately admitted adaptive article mode starts with the complete document and splits only after a confirmed applicable sentence-length rejection. It preserves every source character and successful part, joins verified PCM leaves in source order, and encodes one final MP3 without gain, tempo or pitch filters. Neither mode substitutes a voice. This does not establish how Google processes input internally or guarantee voice compatibility or acceptance.

The live comparison on 6 November 2026 confirmed that this endpoint rejects MP3 output and explicitly requires LINEAR16. Prepare new comparisons as LINEAR16/WAV. Keep the downloaded WAV unchanged for evaluation; an explicitly requested MP3 listening copy is a separate local conversion. The format rejection did not establish voice compatibility.

## Use the configured job

1. Save and select the configured document. In **Create MP3**, choose **Review Long Audio and cost**. This entry appears only for a document included in a verified installed scope. A damaged or unfinished configuration shows an actionable setup message instead.
2. Review the exact saved revision, voice, format, one synthesis estimate, retained commitments, tax/FX provision and cumulative cap. The estimate is not an invoice or a promise of free usage. Storage and transfer charges also count. Adaptive review includes the recovery-family aggregate allowance and remaining holds; failed parent and child commitments remain counted. A generic one-shot scope never grants child-request authority.
3. Choose **Check audio setup**. CloudScribe detects an existing FFmpeg installation and asks you to recognize it. Nothing is downloaded or installed by detection. A file picker remains available as a fallback.
4. Confirm the disclosed private response capture, then choose **Generate Long Audio** for one-shot mode or **Generate complete article MP3** for an explicitly enabled adaptive family. One click starts the selected workflow. Submission and status checking run internally. **Stop after current action** records the current outcome before stopping; it does not cancel or repeat an in-flight paid POST.
5. After private object identity, generation, checksums and full local decoding pass, use the article-specific **Play**, **Save a copy**, or **Open saved audio folder** controls. Reopening the same saved revision uses its receipt; it does not generate again. These controls are separate from the short-text player's selected file.

The installed default package does not admit arbitrary new article jobs. A document must already have its exact configuration and authority before this entry is available.

A known operation can resume with GET-only status checks. An uncertain POST, changed source, failed provider outcome, missing ledger or unavailable required diagnostics cannot become permission for another paid submission. A purely local correction before any claim is kept distinct from an ambiguous submission. There is no separate total-run timer or automatic paid retry.

## Private setup and access

The existing Google account/project must be enabled for Text-to-Speech, with an existing private output bucket. Google documents Storage Object Creator and Storage Object Viewer for the synthesis service account on that bucket. Do not grant project-wide Storage Admin, create a new key, expose objects publicly, or assume that a browser user's access is also the application's access. Security-sensitive access changes require their own authorization.

`INSTALL-CLOUDSCRIBE.ps1 -ReviewedLongAudioScopePath ... -ReviewedLongAudioScopeSha256 ...` provisions only independently reviewed exact scope bytes. This is a separate no-provider phase and requires a closed profile and the intended installed binaries. The installer seals before/after evidence, retains prior narration accounting, publishes the marker last, and supports interrupted resume. Rollback is allowed only before a paid claim or operation/output evidence exists. It never resets a used ledger, overwrites an accepted recording, or fabricates authorization. Private scope, profile, evidence and budget files must not enter the clean share package.

`scripts/Update-LongAudioScope.ps1` provides the narrow reviewed MP3-to-LINEAR16 transition after a definitively rejected HTTP400 response with no returned operation. It requires a new explicit source-approval pin and exact predecessor scope, ledger, marker, provisioning and operation pins. It preserves the failed hold and journal, permits one distinct same-source request, archives the predecessor configuration and supports exact interrupted resume. An uncertain submission, changed prior spending, reused attempt or changed cap is rejected. Its `ValidateOnly` mode changes no live files. This is local maintenance, not a synthesis runner or permission to retry a failed request.

## Evidence and limitations

Each submission/poll is tied to the exact request, project/location, durable operation and retained financial claim. Ordinary logs and permanent trend history retain safe identities, sizes, timing, outcome and capture completeness, without source text, tokens, raw URI or provider prose. Correlation IDs and hashed provider IDs remain distinct. A started send is not proof of provider receipt or billing.

Protected responses are bounded, redacted and current-user encrypted in the normal Windows installation. They may contain article fragments. They are excluded from routine logs, support exports and uploads. Logical retention is 24 hours with deletion on next access; encrypted remnants may remain while the app is closed or in backups. Truncation, omission, failed readback and storage failure are explicit, with no raw-data fallback. HTTP-200 metadata is retained before parsing so local validation errors do not discard received evidence; audio bodies are excluded. Actual storage failures stop required continuation rather than permit a duplicate request.

An operation reporting completion is not verified media. Download binds the exact private bucket/object generation, validates MD5 as a transport checksum and SHA-256 as local identity, and runs the pinned local probe and full decoder. Resume can verify already downloaded bytes without another POST or download. Unknown or failed outcomes remain recorded even when diagnostics or cleanup also fail.

Synthetic tests and native UI fixtures prove only the paths they exercise. Fake provider/native-verifier results do not establish live Google compatibility, actual narration quality or real decoding. A live trial must retain existing commitments, use the authorized cumulative cap, stop on failure/uncertainty and never silently substitute a voice.

## Primary references

- [Long Audio quickstart and storage prerequisites](https://docs.cloud.google.com/text-to-speech/docs/create-audio-text-long-audio-synthesis)
- [v1 synthesis request and operation response](https://docs.cloud.google.com/text-to-speech/docs/reference/rest/v1/projects.locations/synthesizeLongAudio)
- [Operation lookup](https://docs.cloud.google.com/text-to-speech/docs/reference/rest/v1/projects.locations.operations/get)
- [Text-to-Speech pricing](https://cloud.google.com/text-to-speech/pricing)
- [Cloud Storage pricing](https://cloud.google.com/storage/pricing)
