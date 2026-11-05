# Reviewed patch transfer only

These two text patches are review inputs, not installed application changes. Do not merge this transfer PR as a software release.

Both modify GoogleTtsUsabilityHotfixBinder.WholeArticle.cs and require manual integration with the current development checkout. Preserve newer local capture, progress, output and duplicate-prevention behavior. The UI patch was based on b924798166cb8c955bb6dfc745e4bcc0eb188dd2. Do not apply either patch blindly or reset local changes.

## SHA-256 (exact UTF-8 bytes)

- cloudscribe-one-estimate-and-refresh.patch: 8c211a7df9920944fb82f48e609510d66cc54685c6d8c5080e2736a0bab47bff
- narration-scalable-concat.patch: a082ef80e5cb54fc582c01ea0d2be3c0a2db041369a4707a419434c17541105e

## Verification required

Retrieve at the immutable transfer commit and verify both hashes before use. Integrate in a review branch, run the supported .NET build, analyzers, architecture and infrastructure tests, then exercise the Windows UI with synthetic input. The patches were independently statically reviewed; native C# testing and final merged Windows acceptance remain required. The patch files alone do not modify executable source, authorize provider calls, renew diagnostic windows, change spending limits, reset uncertain outcomes, merge main or deploy anything.
