# Existing Google connection repair

Status: production transaction/controller/UI implemented in an isolated source worktree, under offline tests and independent review. It is not installed or admitted for real use. No real credential read/write or provider call occurred in this development phase. PR89 native acceptance remains a separate gate.

## Trust: explicit reconnection, not claimed original-key restoration

Historical equality with a previously imported private key is not technically necessary for a separately explicit reconnection to the intended existing service account. That equality remains unknown unless independently established. Editable JSON identity labels alone do not prove possession or service-account/project access.

The user explicitly chooses the existing local account, a private JSON candidate and the intended service-account email. Verification requires the retained exact project and standard global endpoint binding, strict bounded identity fields, a real RSA public-key fingerprint derived locally, successful fresh OAuth authentication, an authenticated catalog read and the existing input-free capability probe with the exact project header. The probe posts only `{}` and cannot synthesize speech. Failure remains closed; no candidate material is written persistently during verification.

The final review binds the actual verified candidate's public-key fingerprint, declared key ID, intended account/project, old full account snapshot/revision, unchanged financial snapshot, new staging generation, exact loaded build/profile, fresh review and repair IDs and a five-minute expiry. Its key ID is declared metadata, not an independent Google key-ID attestation. The trusted application UI requires an unchecked, separate exact local-write acknowledgement and a final one-use button. File selection and verification are not consent. There is no command-line, remote or generic provider callback repair route. A caller-provided proposal or file cannot establish paid authority.

The older pure proposal builder models a stricter independently anchored original-identity proposal only. It remains non-executable and is not used to invent historical provenance or authorize this explicit reconnection. New/rotated Google keys, IAM changes and another local account remain outside repair.

## User flow and private lifetime

In Setups, **Repair existing Google connection** requires exactly one existing enabled Google account. Enter the intended service-account email and select an existing private JSON locally. The file is not displayed. Beginning another selection, including a later cancelled or failed read, immediately discards the earlier review and consent. Changing identity/file or closing cancels work. A frozen candidate copy and freshness/version lock prevent stale asynchronous verification from publishing an old review.

Choose **Verify intended connection with Google (no speech)** explicitly. After verification, inspect the account, project, service account, declared key ID, unknown historical-key equality and review expiry. **I approve this exact local credential write to my Windows Credential Manager. No speech or key rotation.** starts unchecked. Only **Restore this connection locally once** can invoke the transaction after that acknowledgement. The pending review is consumed once, even on failure; no automatic repeat follows.

The selection buffer is cleared when verification takes ownership, on replacement/cancellation and on close. The owned verification buffer is cleared after its await. The decoded candidate vault is memory-only, discarded on cancel/failure and at expiry (a 30-second cleanup interval), and cleared after commit. Managed parser/token strings are subject to runtime memory management; complete process-memory erasure is not claimed. Secret values, source JSON, tokens and private-key hashes are never logged, exported or stored in the database. The journal records nonsecret identity/public fingerprint and scope only. Diagnostics use fixed categories; no raw provider text or exception message is displayed by the repair UI.

## Persistence, staging and rollback

Fresh onboarding still refuses to overwrite an existing provider account. The lower-level overwrite-capable import service is used only with a private in-memory vault for candidate parsing, never to repair old real vault targets.

A read-only presence interface distinguishes absent roles from present/empty/unknown material without decoding secret values. All three original roles must be absent; partial, unknown or present roles require separate reconciliation. This feature is not an arbitrary overwrite/key-rotation path.

After exact confirmation, acquire an exclusive cooperating profile repair lease, recheck old roles/account/financial state/expiry and create the durable repair journal. The original reference is never written or deleted. Stage under one random new generation; a prepared generation must still be completely absent before staging starts. Each slot is checked before write, present resumed slots must match the same verified candidate, and every slot is read back before commit. Different candidate/public-key identity, collided/altered stage, changed account/financial evidence, cancellation or expiry stops without overwriting or deleting old material.

Windows Credential Manager has no atomic create-if-absent or three-entry transaction. Exclusive app/profile ownership, random namespace and repeated checks protect cooperating writers; arbitrary same-user external vault mutation is not made atomic with SQLite. Linked database/repair-lease paths are rejected. Do not claim vault CAS or physical immutability.

The account-binding compare-and-swap, committed journal state and append-only approval/lineage event are one SQLite transaction. Only the credential reference, account revision and update timestamp change. Logical account ID, display name, project/endpoints/region and all prior authorization/financial/document/audio records remain. The full old account snapshot and each distinct explicit review stay in immutable lineage. A failed transaction rolls back both binding and committed audit. No downgrade or automatic deletion can discard that history.

Before commit, partial or uncertain staging is retained and the old account remains authoritative. Close/reopen repair, select the same candidate, obtain fresh verification and make a new explicit one-use confirmation to resume the same repair ID and matching owned slots. No staged slot is blindly rewritten. If the account CAS committed but the final acknowledgement was lost, read the retained committed status and current binding; do not create another repair to obtain a success message. A committed journal cannot be changed or deleted. Automatic credential deletion or rollback of an already committed binding is deliberately not implemented. If safe resume is impossible, retain staging and reconcile explicitly; Cancel/close does not erase the journal.

## Paid paths and authority afterward

A shared UI operation lock prevents repair entry while normal Google generation or a Voice Lab audition is in flight, and prevents either paid path from entering after repair begins. Active journals also guard new queue/spend/recovery/article/audition claims at the database boundary. Existing uncertain or unconsumed reserved attempts block repair. Holds and expired policies are never cleared, reduced, renewed or rebound.

After mutation starts, the UI keeps old generation controls unavailable. On success close/reopen CloudScribe and refresh real Google voices before use. The new credential reference and account revision invalidate old token/request/capability/approval bindings. Verification does not persist catalog or spending authority, and the repair does not call synthesis. Any later narration needs its ordinary fresh evidence, independently admitted policy and financial approval; an expired earlier admission remains unusable.

## Validation boundary

Synthetic tests use generated RSA keys, a memory vault, a fake HTTP handler (only OAuth, catalog GET and an input-free probe), and isolated migrated SQLite databases. They cover exact consent/cancel, changed or missing identity, project-proof rejection, old-role presence, each staged-write failure and lost acknowledgement, same-ID restart/resume, altered stage, concurrent confirmations, expiry, shutdown cancellation, paid-path races, immutable events, atomic account/audit rollback and retained document/audio/financial data. Private journal content is queried through SQLite rather than reading an open main database file and ignoring its WAL.

Actual native readability/file-picker interaction, real Google verification, Windows Credential Manager writes, installation and live repair remain distinct gates. Do not infer any of them from source/unit tests. The unresolved historical intermittent startup crash is not fixed by this work.