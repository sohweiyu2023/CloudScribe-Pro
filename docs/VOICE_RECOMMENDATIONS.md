# Language-aware voice recommendations

The verified-voice selector offers **Balanced language default** and **Premium Chirp 3 HD (explicit choice)**. Enter an exact provider locale such as `en-US` or `cmn-CN`, then load the catalog from the saved connection. Recommendations only use enabled, authorized, current catalog entries whose provider-returned language metadata exactly matches that locale. No voice is invented from a name, no locale/region is guessed, and no catalog or synthesis call is made by recommendation code.

Balanced English prefers available Neural2, then WaveNet/Standard. Other languages prefer WaveNet, then supported Neural2/Standard. Mandarin has no Neural2 option. A missing family is disclosed by the actual suggested family; if no compatible candidate is available, use the manual list. Within a family, ordinal voice-name ordering is a reproducible starting point, not a claim that a gender or voice letter sounds best. Listen before choosing a narrator.

An unset English selection may receive this in-session default after catalog loading when reference pricing is usable. It is visibly labeled and is not persisted as a preference automatically. Any existing selected or remembered voice, including a remembered voice no longer available, prevents this default. A Chinese voice is never auto-applied, even when it is the only catalog entry. The explicit **Use recommended voice** action can replace the current choice and remember that user choice; the manual verified-voice list remains available.

Premium HD must be selected explicitly and is never an automatic fallback. For `cmn-CN`, Alnilam is the first offered HD candidate **only if** `cmn-CN-Chirp3-HD-Alnilam` is actually present in the current catalog; otherwise another verified HD entry is offered. This does not write a particular person's profile during development. It is a starting suggestion, not a claim of measured voice superiority.

## Price and request compatibility

The panel shows family, exact locale, catalog reference price and its verification date. The authenticated catalog currently distinguishes Neural2 (USD16/million characters) and Chirp3 HD (USD30/million); WaveNet is USD4/million. These are dated references, not a live account quote. Missing, negative, future-dated or expired reference pricing disables recommendation application/defaulting; the manual list and existing guarded cost review remain separate. Reference expiry uses the authenticated catalog's own max_rate_age_days, not a new hidden lifetime. Actual current request cost, trust, account/project/capability evidence and explicit single-use approval are still required before synthesis.

Google's detailed Chirp3 page documents synchronous text/MP3 support and preview SSML/voice controls; its general voice page has older restrictions. CloudScribe deliberately supports the narrow common path: **plain text, MP3, default controls**. Chirp payloads omit speakingRate, pitch and volumeGainDb entirely. Any nondefault prosody node or non-MP3 encoding is rejected, including prosody subsequently reset to defaults. No SSML or preview control support is inferred. Existing Standard/Neural2 request envelopes are unchanged. The short audition comparison panel still treats Chirp as reference-only; this change does not enable Chirp sample generation there.

Official sources checked 2026-10-26:
- https://docs.cloud.google.com/text-to-speech/docs/chirp3-hd
- https://docs.cloud.google.com/text-to-speech/docs/list-voices-and-types
- https://cloud.google.com/text-to-speech/pricing

## Safety and scope

Recommendation/default selection never prepares, approves, queues or submits a request. Selecting a premium family never accepts spend. It does not detect a document's language, transmit its text, change credentials or auto-generate samples. Text review and exact-request approval remain mandatory. Existing live installation/profile and the requested Chinese narration are untouched by development. All feature tests use synthetic catalog entries and local compilation; no paid calls certify provider audio output.

## Chirp character estimates and unchanged reservations

Official pricing counts input characters, including spaces/newlines; JSON escaping and UTF-8 transport bytes are separate. For the supported plain-text Chirp path, the estimator counts Unicode scalar values, rejects invalid surrogates and does not normalize combining sequences. It retains actual input-byte metadata and the separate compiled-byte limit. SSML is rejected for Chirp; preview markup is not enabled. Other family/SSML estimates retain the existing byte upper bound, including all markup, which conservatively includes otherwise exempt mark tags.

Each request's USD subtotal is still rounded upward to a cent. Summing those request ceilings can exceed the full-document unrounded character quote. No aggregate quote replaces per-request reservations. Historical or uncertain holds are neither rewritten nor released. This source correction does not change a user's cap, dispatch a request, or prove that 46 separately rounded parts fit an approved amount.
