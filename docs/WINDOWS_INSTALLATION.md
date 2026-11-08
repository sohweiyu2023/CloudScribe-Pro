# Permanent Windows installation and updates

Create a clean package with `scripts/New-CleanWindowsShare.ps1`, then run:

```powershell
./scripts/Install-CloudScribe.ps1 -PackageRoot <clean-package-folder>
```

Close every CloudScribe window normally first. The installer refuses to kill processes or replace an unrecognized installation. It validates the package, stages a replacement beside the destination, retains the previous install for rollback, and switches only application files. It installs to `%LOCALAPPDATA%\Programs\CloudScribe Pro`; the executable is always `app\CloudScribe.exe` there. The apphost finds `../runtime` directly, so the shortcut targets the real application EXE and needs no command window or global environment change.

The installer creates or updates one per-user Start Menu shortcut named **CloudScribe Pro** with the gold-star application icon. Its target and icon stay at the same path for every update. Run that shortcut to verify the latest binary. For taskbar access, right-click the running gold-star CloudScribe icon and choose **Pin to taskbar**. Windows controls pinning; the installer does not modify undocumented taskbar registry data or claim that a Start Menu shortcut is a taskbar pin. Existing pins to this permanent executable continue to start the latest replacement build.

For a computer whose current launcher selects an older custom profile, inspect that launcher and close the app, back up the profile, then add `-MigrateProfileFrom <existing-profile-folder>` **only on the first installation**. The installer copies and verifies the entire closed profile into `%LOCALAPPDATA%\CloudScribe Pro\Profiles\Studio`, refuses any existing migration destination, and leaves the source untouched. It changes only the installed private `appsettings.json` to select that profile. It does not overwrite another default profile, touch Windows Credential Manager, move generated MP3s or change credential references. Later updates omit migration and preserve the installed profile selection. The default profile, a migrated Studio profile and generated audio all live outside Programs and are never replaced by app updates.

Use `-ValidateOnly` to run preflight without changing files or shortcuts. Test malformed packages, profile conflicts, a running app and a second update before treating a new installer revision as verified. The source package must have a null profile override and must come from a clean publish; never build a friend's ZIP from the used permanent installation. Installed settings, rollback folders and profiles are private. No machine-specific path belongs in Git.

Use `-ValidatePackageOnly` to check just a clean package, without inspecting installed files, profiles or shortcuts. This mode cannot be combined with `-ValidateOnly` or migration. Packaging runs this check before reporting success. The installer accepts the project `LICENSE`, the exact third-party inventory, and only its hash-verified notice files; it pins the reviewed license and inventory so package contents cannot expand that allowlist. When updating those source files, review them and refresh their installer pins together. Run `scripts/Test-InstallCloudScribe-SelfTests.ps1` and `scripts/Test-ThirdPartyNotices-SelfTests.ps1` to check valid and rejected local fixtures. These checks do not install the app or establish publisher authenticity.

The executable and window icon are generated from the existing Studio four-point star by `scripts/New-AppIcon.ps1`. The checked-in multi-resolution ICO is embedded both as a Windows application resource and an Avalonia window resource.

## Installations with Long Audio history

The ordinary installer refuses to replace an installation that already carries Long Audio authority or history. This prevents an application update from silently discarding its scope, spending claims or unresolved request evidence. Use the supported, independently reviewed build-only maintenance path with the exact tested package and genuine candidate-native continuity evidence. Do not delete a scope, marker, ledger or pending guard to force an update.

A build-only transition changes build/source bindings, not approved jobs, spending caps, expiry or permission. It preserves existing deferred evidence and any already-approved regeneration record, retains a closed-profile backup and previous application, and verifies the resulting installation. A historical expiry field alone does not determine validity when an existing completion-bound continuity policy applies; use the current policy validator. New narration or renewed authority remains a separate review.

## Backup and rollback limits

The installer returns `PreviousInstall`, the exact retained previous application directory. Keep that path and a hash-verified closed-profile backup with the update record. It retains application binaries; it does not create a complete rollback of documents, account metadata, audio, authorizations or budget state. The profile and generated/exported audio remain outside the application directory.

If an update fails before the staged folder switch completes, the installer restores the previous application directory where possible. After a completed update, rollback is a separate reviewed operation: close CloudScribe normally, verify the returned previous directory and its hashes, confirm profile/schema compatibility, and retain a fresh backup before switching binaries. Do not install directly from a used `PreviousInstall` directory as though it were a clean share package. Do not blindly replace the current profile with an older backup: that could erase newer documents, audio references, spend reservations or reconciliation evidence. Keep both application versions and profile backups until a compatible rollback plan is verified. Never regenerate paid audio merely to recover a playback shortcut.


An update that adds durable submission-attempt barriers treats historical queue rows conservatively as already attempted. It does not infer that any historical request was free and does not reset reservations. An older executable may ignore those safeguards even if its database reads still succeed; binary rollback therefore requires a specific compatibility and spend-safety review, not merely a successful startup.

## Reviewed internal article admission

The optional `-ReviewedChirpArticlePolicyPath` and `-ReviewedChirpArticlePolicySha256` arguments form a separate, independently reviewed private admission boundary for [Chirp article narration](CHIRP_ARTICLE_NARRATION.md). Both are required together. The installer checks exact package application/infrastructure hashes, inspected profile identity, expiry, strict bounded schema, native coverage, financial pins and request limits before staging anything. It refuses simultaneous article and historical-recovery policies. `-ValidateOnly` checks admission preflight without installing or granting it.

An admitted policy is kept outside `app` beside the installation marker with its exact digest pinned in that marker. It is private evidence, never part of a clean package or public source. Runtime readers recheck build/profile/pin/expiry and exact live native/financial evidence; a plan file or UI checkbox alone grants no authority. An ordinary update does not inherit a prior policy. Retained article attempts remain in the profile regardless of policy expiry and require spend-safe compatibility review before rollback. Do not edit the policy, marker or database to renew permission or clear holds.

## Add a separate exact article admission

The 1.0.5 adds a separate private admission transaction for new exact comparison documents on an existing installation. First complete the independently reviewed build-only transition to the exact candidate. Then review the additive plan and its SHA-256 separately. Do not combine additive admission with the build update, migration, Long Audio provisioning or another authority mode.

```powershell
./scripts/Install-CloudScribe.ps1 -PackageRoot <reviewed-clean-package> -ReviewedAdditiveArticlePlanPath <private-reviewed-plan.json> -ReviewedAdditiveArticlePlanSha256 <sha256> -ValidateOnly
./scripts/Install-CloudScribe.ps1 -PackageRoot <reviewed-clean-package> -ReviewedAdditiveArticlePlanPath <private-reviewed-plan.json> -ReviewedAdditiveArticlePlanSha256 <sha256> -AdditiveArticlePhase Install
```

Close CloudScribe normally and retain the required closed-profile backup before this operation. `-ValidateOnly` performs preflight without granting authority or submitting speech. `Install` requires the exact reviewed source approval, native document/revision proof, plans, financial baseline and candidate build/profile bindings. The current plan format covers two distinct saved `en-US` documents with identical source text, one for `en-US-Neural2-D` and one for `en-US-Chirp3-HD-Aoede`, within their separately reviewed shared allowance. It supplies no default spending approval. The app still requires each document's exact cost review and approval before generation.

The transaction appends immutable grants and receipts to a private article catalog beside the installation. It publishes the catalog registry last and does not rewrite the existing installation marker, older policies, Chinese narration caps, Long Audio ledger, request history or accepted audio. New documents cannot borrow an older document's admission or erase its uncertain holds.

After an interrupted operation, inspect its state first. `-AdditiveArticlePhase Resume` accepts only the same reviewed plan and exact retained evidence. `-AdditiveArticlePhase Rollback` is limited to an unpublished transaction and preserves its audit history. Once the catalog registry has been published, rollback is refused even if its authority appears unused. Do not delete private catalog files or edit the marker to work around that refusal.

A later build-only transition must use version 3 of the reviewed build plan, explicitly pin the catalog sidecar and append a new build-binding receipt while retaining immutable grants, policies and request identities. Legacy version 1/2 build plans refuse an existing additive catalog. This transition does not renew expiry, increase a cap or authorize another request.

These private plans, native/source proofs, catalog entries, receipts and journals must stay outside clean packages and public source. The candidate's source changes alone do not install or admit anything; exact-build tests and isolated native acceptance remain required before release.
