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

## Backup and rollback limits

The installer returns `PreviousInstall`, the exact retained previous application directory. Keep that path and a hash-verified closed-profile backup with the update record. It retains application binaries; it does not create a complete rollback of documents, account metadata, audio, authorizations or budget state. The profile and generated/exported audio remain outside the application directory.

If an update fails before the staged folder switch completes, the installer restores the previous application directory where possible. After a completed update, rollback is a separate reviewed operation: close CloudScribe normally, verify the returned previous directory and its hashes, confirm profile/schema compatibility, and retain a fresh backup before switching binaries. Do not install directly from a used `PreviousInstall` directory as though it were a clean share package. Do not blindly replace the current profile with an older backup: that could erase newer documents, audio references, spend reservations or reconciliation evidence. Keep both application versions and profile backups until a compatible rollback plan is verified. Never regenerate paid audio merely to recover a playback shortcut.


An update that adds durable submission-attempt barriers treats historical queue rows conservatively as already attempted. It does not infer that any historical request was free and does not reset reservations. An older executable may ignore those safeguards even if its database reads still succeed; binary rollback therefore requires a specific compatibility and spend-safety review, not merely a successful startup.

## Reviewed internal article admission

The optional `-ReviewedChirpArticlePolicyPath` and `-ReviewedChirpArticlePolicySha256` arguments form a separate, independently reviewed private admission boundary for [Chirp article narration](CHIRP_ARTICLE_NARRATION.md). Both are required together. The installer checks exact package application/infrastructure hashes, inspected profile identity, expiry, strict bounded schema, native coverage, financial pins and request limits before staging anything. It refuses simultaneous article and historical-recovery policies. `-ValidateOnly` checks admission preflight without installing or granting it.

An admitted policy is kept outside `app` beside the installation marker with its exact digest pinned in that marker. It is private evidence, never part of a clean package or public source. Runtime readers recheck build/profile/pin/expiry and exact live native/financial evidence; a plan file or UI checkbox alone grants no authority. An ordinary update does not inherit a prior policy. Retained article attempts remain in the profile regardless of policy expiry and require spend-safe compatibility review before rollback. Do not edit the policy, marker or database to renew permission or clear holds.
