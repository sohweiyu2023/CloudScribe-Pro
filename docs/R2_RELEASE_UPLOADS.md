# Upload and verify a tested Windows release

Publish only when the maintainer has authorized the release in the conversation. A source or documentation cleanup does not itself require a binary rebuild or upload. Use a tested clean package from [Safe Windows sharing](SHARING_WINDOWS.md), keeping the working installation, profile and credentials intact.

## Prepare the immutable artifact

1. Record the exact source commit, locked restore, strict Release build, format and test results, and actual native/UI verification limits. See [Windows build and validation](../BUILDING-WINDOWS.txt).
2. Publish to a new clean staging directory, then package with `scripts/New-CleanWindowsShare.ps1` and a compatible private runtime. Smoke-test a separate copy so execution logs cannot enter the upload artifact.
3. Audit every ZIP entry. Exclude profiles, databases, documents, generated audio, recordings, keys, credentials, preferences, logs, QA evidence and rollback directories. Inspect JSON configuration: only public default appsettings and required dependency/runtime manifests belong in the app, with no personal profile override. The separate public `licenses/third-party/components.json` is dependency/license metadata, not app configuration. Retain the project LICENSE, third-party notice index and original license texts, plus the runtime notices. Compare the packaged app DLL with the tested build and the default configuration with the repository.
4. Compute exact ZIP byte size and SHA-256. Keep the audited ZIP unchanged through upload and public-download verification.

## Inspect the destination before writing

Use the maintainer's existing authorized Cloudflare session or already-configured scoped credentials. Inspect the intended bucket, its configured public domain and current release object before selecting or replacing a key. Preserve unrelated objects. Do not create credentials, expand access, change browser/security/domain settings, extract session secrets or bypass tool restrictions to complete publication. A login in another browser does not establish the state of the authorized session.

If a previous operation timed out or has an uncertain result, inspect and download the object before retrying. An existing object with matching bytes needs no duplicate upload. If supported access or file attachment is blocked, retain the audited ZIP and report the exact pending action; use a maintainer-performed upload when necessary.

## Upload and verify

Upload only the audited ZIP through supported controls or an existing scoped API/CLI configuration. Inspect the resulting object key, size and upload status. Derive the URL from the domain actually configured for that bucket. Download the public object to a separate verification file and compare its exact size and SHA-256 with the audited ZIP. HTTP success alone is insufficient; a stable URL can serve cached older bytes.

Record the source commit, test evidence, object key, verified public domain/URL, exact ZIP size/hash and verification time without secrets. Return the GitHub source link separately from the binary download. Upload completion does not authorize live blog edits.

## Historical verified release: 20 October 2026

The existing maintainer release record reports the following successful public byte verification. This is a dated record, not a guarantee of the current contents of a mutable URL; recheck before any future replacement.

- Object key: `CloudScribe-Windows-x64.zip`
- Public binary: https://files.awakeningtoreality.com/CloudScribe-Windows-x64.zip
- Source: [ffc1b13 / PR71](https://github.com/sohweiyu2023/CloudScribe-Pro/pull/71), full commit `ffc1b13e4075784fbef91a3c20d93f3e610e2e72`
- Recorded checks: strict Release build, format and 718 compiled tests, with native/UI limits recorded separately.
- ZIP size: 53,414,202 bytes
- SHA-256: `564DAF4750FA0F3AED1BE728A0023655C40D794F04A0BD31FEF507066E68BF9B`
- Public download matched the audited ZIP at `2026-10-20T13:34:24.7692516Z`.
