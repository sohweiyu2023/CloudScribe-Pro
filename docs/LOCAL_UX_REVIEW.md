# Local usability review — 20 October 2026

Historical review of the earlier six-step help increment. Later updates replaced it with the live 15-step first-connection / 11-step saved-connection walkthrough and corrected subsequent voice-loading behavior. Use [the user guide](USER_GUIDE.md) and [current walkthrough](VISUAL_WALKTHROUGH.md) for present instructions; the findings below describe this increment only.

This increment follows main `7fb8e59d0f191a14c9540512b1c0d0d61a6288fc` and the earlier request/save safety review. It addresses the returning user's report that the connection looked unsaved and creating an MP3 was hard to find. No paid synthesis is part of its verification.

## Findings and changes

- The real Google account binding and AtR Standard C voice setup persisted. Blank inputs belonged to fresh configuration. The saved connection now has a concise status; its configuration form is hidden until **Change connection** is chosen in Advanced tools.
- **Create MP3** is a primary toolbar and Studio-inspector action. It opens one guided window for saved voice choice, catalog refresh, exact cost review, explicit consent and creation. The cost, consent, creation action and result status stay in a fixed footer.
- The duplicate disabled Studio generation action is removed. Missing prerequisites produce next-action messages. Creation still uses the existing exact compiled request, current evidence, explicit single-use authorization and durable submission engine.
- Ordinary preferences survive restart. Last-opened document identity is now remembered, with fallback to the current recent library ordering if it is absent. A saved voice remains untrusted intent until an exact refreshed catalog match is returned.
- Play, verified export and saved-folder access are grouped in **Listen, save and find audio**. Stop waiting appears while generation is active and explains possible charges. Dialog-owned subscriptions are released on close.
- **Help** opens a six-step walkthrough inside the application. It appears automatically for a fresh profile and can be skipped, finished and reopened. Native checks cover its setup links, all six steps, Enter/Escape, retained skip preference and unchanged edited text after dismissal. Safe sharing instructions require each recipient's own provider/billing access.

## Coverage and limits

| Area | Verification in this increment | Material boundary |
|---|---|---|
| Navigation and first use | Native empty profile; Create MP3 visible; empty-document guard; existing adaptive-shell and shortcut tests | No full device/monitor or screen-reader certification |
| Returning user and connection | Native saved profile status and named preference; restart checks; metadata only | Credential values never inspected; no credential replacement, account management or access expansion |
| Create MP3 approval | New tests reject missing consent, stale review, reused review and edits during approval; exact ceiling reaches the existing runtime once | Native testing avoids live approval/dispatch; no paid synthesis in this update |
| Document import/edit/save/reopen | Existing format, autosave, checkpoint/conflict and Unicode tests; native harmless text/autosave/restart | TXT/Markdown/HTML/DOCX/clipboard; PDF/OCR not supplied; recent-list fallback applies |
| Voices, samples and setups | Existing catalog authorization, exact preferred match, sample cache and setup persistence tests | Catalog refresh is explicit; access evidence expires; samples require separate approval |
| Pricing/readiness | Existing provenance, bounded-source, rounding and runtime revalidation tests; visible local status | Source fetch is not automatic rate verification; estimates exclude tax/credits/conversion |
| Generation errors and recovery | Existing cancellation, stale capability, duplicate/uncertain transition and private-error regression coverage | Lost synchronous responses need provider outcome evidence; no safe automatic reset/retry UI |
| Audio/export | Existing accepted-byte/tamper/cache tests plus local existing-MP3 verification | Trusted in-app playback state remains session-only; old files use normal local playback |
| Long text | Existing lossless compiler-based local-part tests and prior native four-part verification retained | No billed batch orchestration, ordered resume or MP3 assembly |
| Help/settings/accessibility | Native first-run and reopened six-step help; Enter/Escape; edit preservation; named controls; existing keyboard/adaptive-shell tests; theme preferences retained | No quantitative contrast/animation certification, shortcut customization or full settings wizard |
| Clean sharing | Allowlisted app/runtime-only package with no personal workspace override; fresh native startup loads its bundled runtime | Windows x64 portable folder; not a signed installer; no live setup on another person's machine |
| Diagnostics/data upgrade | Existing redaction/support-bundle exclusions; separate app folder; closed-profile backup and preservation checks | Review is not a penetration test; vault is separate from ordinary profile backup |

Exact local delivery reports and private native evidence are not distributed with the source. For current launch instructions see [Windows installation](WINDOWS_INSTALLATION.md); the release record is in [Release uploads](R2_RELEASE_UPLOADS.md). Larger unsupported workflows are reported as limits rather than implied by passing library tests.
