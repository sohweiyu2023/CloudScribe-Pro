# CloudScribe development and local delivery

- Preserve existing documents, audio, preferences, account metadata and Windows Credential Manager entries. Do not make paid provider calls during development or tests unless explicitly authorized.
- For Windows delivery, inspect the current installation and shortcut targets before changing them. Close the app normally and make a closed-profile backup before a migration. Never kill a running app just to replace binaries.
- Publish to a clean staging folder, run the relevant Release build/tests/format checks and native UI smoke tests, then make a clean package with `scripts/New-CleanWindowsShare.ps1`.
- Install/update through `scripts/Install-CloudScribe.ps1`. Keep the executable at `%LOCALAPPDATA%\Programs\CloudScribe Pro\app\CloudScribe.exe` and the Start Menu/taskbar shortcut target stable. A normal update preserves the installed profile override. Use `-MigrateProfileFrom` only for an inspected first migration; never substitute an empty profile for existing work.
- Verify launch through the actual Start Menu shortcut, the loaded runtime and application hash after updating. If the user has pinned this stable executable, keep that same path so the pin starts the latest build. Windows may require the user to choose Pin to taskbar; report that accurately and do not use undocumented taskbar registry hacks.
- Never commit or distribute a user profile, database, service-account JSON, Windows credentials, recordings, documents, installed private appsettings, local evidence containing personal content, or rollback directories. Share only a fresh clean package. See `docs/WINDOWS_INSTALLATION.md` for the update/rollback layout.
- Push/PR/merge requires authorization in the conversation. Preserve unrelated checkout changes; use an isolated checkout when publishing a reviewed local update.

## Production safety and source governance

- Use one coherent exact compiled request and current account/project/voice/capability/pricing/revision/estimate/trust/queue/reconciliation evidence. Preserve explicit single-use spend approval and durable authorization before publishing executable state. Never reconstruct payload bytes from hashes, combine unrelated latest values, auto-approve, fabricate evidence, use ambient credentials/endpoints, or bypass the authorized submission chain.
- Voice Lab revalidates authorization and the exact current catalog selection immediately before submission. Its generic production audition path remains fresh-only; the separate Google sample cache accepts only integrity-verified Stage6 outputs. Never substitute an unverified cache or raw provider callback.
- Recovery validates canonical absolute journal/staging/backup paths before vault access, rejects overlapping roots and journals inside staging/backup, and rejects reparse points in the restore root, its ancestors and destination components before writes. Verify terminal journal state and real filesystem bytes/hash/length before claiming success.
- Retain fail-closed missing/stale/mismatch tests, strict analyzers, formatting and certification gates. Check results on the exact candidate SHA; historical status notes or stale lane heads do not qualify current source. Source acceptance, compiled tests, actual Windows execution and full certification are separate evidence.
- `SESSION_STATE.json` preserves historical Stage8 1.0.0 governance while the app is 1.0.1. Keep the exact SDK pin and control authentication intact. Retained `docs/STAGE*-BATCH*.txt` records include source-verifier inputs. All eleven `controls/v2.22/clean-carrier/*.b64` parts are required authenticated inputs.
- After freezing source/doc changes, run `python tools/update_sha256_manifest.py`, then its `--check` mode. Keep backups, test logs and release artifacts outside the source tree; see `BUILDING-WINDOWS.txt` for validation commands.

## Public Windows releases

- Follow [the release upload procedure](docs/R2_RELEASE_UPLOADS.md) only when the conversation authorizes publication of a tested clean package. Inspect the existing release object before replacing it, preserve unrelated objects, and check uncertain outcomes before retrying.
- Use only an existing authorized session or scoped configuration. Do not create credentials, expand access, change browser/security/public-domain settings, extract session secrets or bypass tool restrictions to finish an upload. If access is unavailable, retain the audited ZIP and report the pending action.
- Verify the public download's exact size and SHA-256 against the immutable audited ZIP before claiming success. Keep source and binary links separate. Release uploads do not authorize live blog edits.
