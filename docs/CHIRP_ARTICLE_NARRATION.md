# Chirp 3 internal article narration

Chirp 3 can reject sentence content even when the request fits the byte limit. CloudScribe's **Internal article narration (Chirp 3)** view offers smaller requests taken from the saved original article. It preserves every character, whitespace, source order and final cutoff. It creates no extra Library documents.

The planner uses existing sentence/clause punctuation and whitespace within a maximum window of 333 Unicode scalars. This is a local conservative heuristic, **not a published Google sentence limit or a guarantee of acceptance**. A long span without a usable existing boundary fails closed; the planner never inserts punctuation or changes wording. UTF-8 input/envelope limits and character-based cost estimation remain separate checks.

## Review, reserve, generate once

1. Save and select the original article, exact verified Chirp voice, account and project.
2. Open **Internal article narration (Chirp 3)**. A separately reviewed installer-pinned policy must admit the exact native document/revision, complete lossless plan, compiled request hashes, old attempt lineage and retained financial baseline. A caller-supplied plan or dialog acknowledgement cannot establish that authority.
3. Select one admitted unsaved request and choose **Review one selected internal request**. Review its current price, maximum reservation, exact payload identity, retained commitments and admission expiry.
4. Explicitly acknowledge the historical holds, then choose **Reserve displayed request only**. This transaction stores the plan, one reservation and its clean queue identity. It sends nothing to Google. **Generate** remains a separate explicit action under the existing current-account, project, pricing, capability, trust and queue checks.
5. Inspect the result before preparing another request. A failure or uncertain outcome stops further requests under this grant. Diagnostics cannot authorize another request or release a hold.

Closing a review before reservation cancels only that review. Closing or restarting after reservation retains it. Fresh review can resume the **same unconsumed identity** under unchanged current admission without another reservation. Consumed identities cannot be replayed. Changed or expired admission fails closed; there is no automatic renewal, retry, descendant creation or background batch.

Each request retains its own upward cent-rounded reservation. All prior failed/uncertain holds remain counted once in the independently reviewed baseline; overlaps with several new slices do not multiply or erase those holds. Legacy paid routes are blocked for an account with active article admission or retained internal reservations so they cannot omit the new financial history. A later budget reconciliation requires a separately reviewed supported change, never deleting rows.

## Accepted audio, restart and final export

Verified previously accepted audio can cover its exact original range once, with original native IDs, text/voice/request identity and file/receipt hashes checked. It is reused without import repetition or regeneration. New accepted responses use an append-only output journal, an owned local audio snapshot and verified private files. Caller cancellation does not cancel the bounded local completion of already accepted audio. A local write failure retains one full response in the running session for repair. **Recover saved local outputs (no provider call)** completes a verified local journal/file/receipt without paid authority. A hard process loss before bytes reach durable storage is not claimed recoverable; preserve its financial hold and investigate rather than regenerating.

The view shows saved/reused coverage and incomplete status. **Export complete article MP3** is enabled only for complete exact coverage. It rechecks native revisions, order, duplicates, file hashes and receipt provenance, then uses the existing FFmpeg assembly/probe/decode and new-destination file picker. Each article exports as one continuous MP3; internal requests remain an implementation detail. Originals are preserved and export cannot synthesize missing audio or change the ledger. See [article MP3 export](ARTICLE_MP3_EXPORT.md).

Private one-response diagnostics remain default-off and separately consented. If enabled, they observe the exact already reserved slice, not a reconstructed hash or an unrelated current document. They do not appear in normal logs or public source. See [Google failure diagnostics](GOOGLE_FAILURE_DIAGNOSTICS.md).

## Delivery and verification limits

The additive migration retains all existing document, revision, group, queue, spend, recovery and grant rows. Its internal plan/attempt/journal/receipt history cannot be discarded by a downgrade. Keep a closed-profile backup and retained application rollback; older binaries do not understand these retained reservations, so successful startup alone is insufficient for a safe rollback.

Offline tests use synthetic profiles, independently pinned fixture policies and fake provider/media tools. Passing them establishes local ordering, transaction, replay, preservation and export control behavior; it does not prove Google accepts the heuristic, that real audio has played/decoded, or that an intermittent historical startup crash is fixed. Paid execution needs its own current exact admission and user-approved financial scope. No private policy, article text, audio or local financial evidence belongs in Git or a clean share package.
