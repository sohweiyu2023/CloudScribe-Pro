# Autosave verification

The existing editor binding is two-way (`PaperTextBox.Text` to `DocumentText`). Text/title changes mark the document dirty and queue a 750ms debounced local save. Normal close uses `PrepareDocumentCloseAsync`; a failed/conflicting save or newer edits keep the editor open. Existing tests already cover newer edits during a close checkpoint and failure preservation.

Added two durable integration cases using only a generated temporary AppPaths root, real SQLite migrations and EfDocumentLibrary revision files, synthetic Chinese/emoji/combining/newline text, and fresh ShellViewModel/StudioPreferencesStore instances:

1. Paste-equivalent model text replacement plus edits, await debounce, confirm persisted exact text, close and reopen.
2. The same edits followed by an immediate close while debounce is pending, then reopen.

Both verify last-document recovery from a freshly loaded preferences file, title/text equality, no dirty state after recovery, a further edit/checkpoint, and no prepared spend/generation action. Existing autosave coordinator coalescing/checkpoint tests were also rerun. No autosave production defect was reproduced, so no production autosave code was changed.

This is model/storage verification of the bound text path, not an actual OS clipboard or native-window test. No real profile/document, installed app, provider or credential store was accessed by these cases. Test directories use validated unique GUID descendants of the synthetic temporary root and reject linked cleanup entries.
