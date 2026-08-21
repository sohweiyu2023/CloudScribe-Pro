# CloudScribe Pro Batch, Limits, Autosave and Settings Contract v2.22

## Decision

CloudScribe Pro supports durable multi-item collections. Users may paste several independent texts, add multiple files, add folders recursively after preview, or add existing library documents. The app estimates every item and the entire collection before any billable generation begins.

## Batch preflight and approval

- Each item shows source title, transformed units, estimated duration, provider/model/voice, output plan, cache reuse, estimated cost range, applicable limits, warnings and blocking errors.
- The collection summary shows totals by currency/provider/meter, free or included allowance allocation, estimated overage, tax/FX uncertainty, disk requirement, request count and completion-time range.
- Shared allowances, tiers, minimum charges and account-local usage are allocated deterministically in queue order. Reordering can change the estimate and triggers recalculation.
- Generate All remains disabled until every enabled item has a current estimate or is explicitly excluded.
- Editing text/settings, changing order/provider/account/catalog, consuming a shared allowance elsewhere, or aging beyond the policy window invalidates affected estimates.
- Reapproval is required when cost rises beyond the configured absolute or percentage threshold, currency/price state changes, an unknown limit appears, or an alternative provider would receive content.
- Final reconciliation stores estimate-at-approval, provider usage, invoice/imported actuals and variance per item and for the collection.

## Limits and large text

CloudScribe has no small arbitrary total-text ceiling. It processes long content through safe chunking, provider batch/long-audio operations and durable resume. Practical capacity remains bounded by local storage, memory-safe import limits, queue policy, provider hard request limits, account quotas, rate limits, output-duration limits and user budgets.

The UI distinguishes:

1. Provider/model hard content limits - automatically chunk when semantics can be preserved; otherwise block.
2. Operation limits - choose synchronous, streaming, batch or long-audio only when supported.
3. Rate/concurrency limits - throttle and queue; never evade limits through unsafe parallelism.
4. Account/plan/project quotas - read live headers/API where available, otherwise show provider-console verification.
5. Pricing allowances and budgets - warn, require confirmation or stop according to policy.
6. Application safety limits - protect disk, memory and UI responsiveness; advanced overrides are local and explicit.

Provider/model limits are updated through the signed Pricing/Capability/Limits Catalog schema 1.1.5. The catalog is the dated provider-fact authority; it is not the authority for local application safety policy. Account-specific values are not guessed from JSON: runtime headers, provider APIs, console values and user-entered overrides have explicit provenance and precedence. App safety defaults live in the separate runtime-policy JSON. Explicit user overrides are not overwritten by catalog or runtime-policy updates; conflicts are surfaced for review.

## Save and recovery

- New pasted or imported content becomes a durable draft immediately after validation.
- Text autosaves after the configured debounce, with visible Saving, Saved and Save failed states.
- Ctrl+S creates an explicit checkpoint/revision; Save As exports or duplicates but is not required for ordinary persistence.
- Queue add/remove/reorder, per-item settings and approval records commit transactionally as they change.
- Closing is silent when autosave succeeded. If saving failed, closing is blocked until Retry, Save copy or Discard is explicitly chosen.
- Completed audio is atomically finalized into the Audio Library and optionally copied/exported to the chosen folder. Partial or corrupt files never replace completed output.

## Settings surfaces

The screenshots supplied by the product owner are treated as a useful settings checklist, not a UI to copy. CloudScribe provides:

- General: launch/restore behavior, theme, language, updates, notifications, keep-awake/network policy and privacy.
- Document: skip or transform parentheses, square brackets, braces, URLs, citations, footnotes/endnotes, page headers/footers, page numbers, code blocks, tables and custom rules, all default-off with before/after preview.
- Pronunciation: global/provider/language/document dictionaries, replacements, IPA/phonemes where supported, regex timeout, conflicts and import/export.
- Voice: provider/model/voice, speed, pitch, volume, style/emotion, sentence/paragraph/chapter pauses and preview; unsupported controls are disabled with reasons.
- Audio: MP3/WAV/M4A/M4B, bitrate/sample rate/channels when applicable, mastering, silence trim, chapter/heading/document splitting, cover art and metadata, filename template, destination and output-name collisions policy.
- Shortcuts: discoverable and remappable keyboard/media shortcuts with conflict detection and reset.
- Accounts: credentials, region/project/plan, live connection/quota test, last checked time and billing/console links.

Settings inherit Global -> Profile -> Collection -> Document/Item. Every override can be reset to inherited, previewed and included in the immutable generation snapshot.


## Additional standard safeguards

- Intake detects exact duplicates, probable duplicates, unsupported files, archive bombs, inaccessible paths, duplicate normalized paths and output-name collisions before approval.
- A collection can be paused, resumed, reprioritized, duplicated as a template, partially rerun, or exported as a portable manifest without embedding credentials.
- Every item has an independent outcome; one failure does not erase successful outputs, and Generate Failed Items re-estimates only the affected subset.
- Collection history records who/what approved the estimate, the catalog/account snapshot used, estimate expiry, actual provider usage, output hashes and reconciliation state.
- Scheduled or unattended generation is opt-in, obeys quiet hours, metered-network and power policy, and never bypasses a newly blocking limit or material-cost reapproval.


## Duration-target part export and observability

Duration-target splitting and logging are controlled by `CloudScribe_Pro_Duration_Partitioning_and_Observability_Contract_v2.22.md` and runtime-policy schema 1.3. Duration parts are assembled from actual generated segment durations; pre-generation duration is an estimate. Diagnostic logs exclude text, SSML, audio and secrets, while billable submissions require a durable audit-ledger write.
