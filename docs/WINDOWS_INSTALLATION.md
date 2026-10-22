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
