# Give a clean copy to another person

The **Help** button opens the in-app Quick Start. A new profile opens it automatically; Skip, Done and Escape return to the editor. Your friend connects their own Google Cloud project and billing account.

Distribute only a clean application build and a compatible private .NET runtime. Do not copy your installation workspace, app-data profile, database, documents, MP3s, service-account JSON, Windows credentials or a launcher that points to your personal data.

From a clean publish and a compatible Windows x64 runtime folder, create a new package:

```powershell
./scripts/New-CleanWindowsShare.ps1 -PublishedApp ../CloudScribe-Windows -RuntimeRoot C:/path/to/private-dotnet -OutputDirectory ../CloudScribe-Windows-x64
```

The script copies application binaries, approved static JSON files, Windows native dependencies, the .NET runtime and its notices, the project MIT license and versioned app-dependency notices, a launcher with no personal workspace path, and the user guide. It verifies dependency and notice hashes before and after copying. It excludes application profiles and debug symbols. Choose a new output directory; it refuses to replace an existing directory. When dependencies change, review their licenses and refresh the component inventory first; see [Third-party notices in the source repository](https://github.com/sohweiyu2023/CloudScribe-Pro/blob/main/THIRD-PARTY-NOTICES.md).

Test the resulting folder with a fresh isolated profile before distributing it. This is a portable Windows x64 folder, not a signed installer. Extract the entire folder and run `START-CLOUDSCRIBE.cmd`. Keep the runtime and app folders together. Each user's normal Windows profile stores their own subsequent settings and documents separately.

Smoke-test a copy so runtime logs cannot contaminate the final ZIP. Inspect every ZIP entry and configuration before sharing; the script's extension checks do not replace this audit. For authorized public releases, follow [Release uploads in the source repository](https://github.com/sohweiyu2023/CloudScribe-Pro/blob/main/docs/R2_RELEASE_UPLOADS.md).
