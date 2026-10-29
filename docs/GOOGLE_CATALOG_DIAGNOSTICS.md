# Diagnosing a saved Google voice refresh

In **Setups** or **Create MP3**, use **Refresh real Google voices** for the saved connection. This loads the real catalog and verifies the existing synthesis capability with an intentionally invalid, input-free `{}` request. It does not submit article text, generate speech or authorize a paid request. A failed refresh clears the current selection and remains closed to generation.

The interface reports a fixed diagnostic category and stage. When available, it also shows the numeric HTTP status observed by the app and an allowlisted standard OAuth error category. These are diagnostic observations, not instructions to retry, replace keys, change billing or relax access controls.

## Local evidence

The existing bounded local logs retain events6200-6202 with:

- A random local refresh correlation ID, distinct from a provider request ID or spend authorization.
- Fixed stages for account lookup, credential identity, endpoint binding, catalog token/request/read/parse, capability probe, account revalidation, persistence and UI query.
- Numeric HTTP status from the actual response boundary, safe exception category and numeric SQLite error codes when relevant.
- Standard OAuth rejection categories only, such as `InvalidGrant`, `InvalidClient` or `InvalidScope`. Unknown, malformed, duplicated, oversized or unreadable fields remain classified as such.

The UI-triggered refresh shares its correlation ID through bootstrap, catalog/probe, persistence and UI query. An OAuth token refresh is a nested credential operation with its own local correlation ID; its typed rejection context is retained on the original exception for the enclosing refresh and UI. Existing log timestamps, process/session/build metadata and file/directory limits remain in effect. Logging failures must not alter a refresh result or replace its original failure.

These events exclude credentials, tokens, account/project identifiers, credential references, endpoint query strings, request/article text, response bodies, provider descriptions and exception objects/messages. Reading an optional OAuth failure body is limited to8192bytes and a two-second diagnostic deadline. Only exact allowlisted `error` values are retained; raw values and `error_description` are discarded. No new telemetry or upload is enabled.

## Interpret observations conservatively

An HTTP response at `OAuthTokenRequest`, `CatalogRequest` or `ProbeRequest` identifies the responding step. A later parse/storage/verification failure is a different class of failure. The last logged stage alone does not establish an underlying cause, and an absent HTTP status does not prove that no request reached the service.

The `saved-credential-unavailable` category means the configured credential reader returned no usable material for a required saved role in the current Windows context. It does not prove deletion or identify why that material is unavailable. Check the Windows account and saved connection; retain the original approved service-account file for secure recovery. Do not create replacement keys, invent metadata or erase the saved account/financial history merely to retry.

A remembered voice name or exact saved payload cannot manufacture current catalog, account, project or capability authority. Previously persisted evidence can survive restart only while it still passes its existing freshness and binding checks. Bootstrap evidence expires after30minutes; the lifetime and admission requirements are unchanged. Refresh through the supported saved connection rather than editing catalog/authorization rows, replaying stale evidence or fabricating a selection.

For a controlled check, preserve documents and spending holds, run one refresh, then inspect its result and local diagnostic stages. Stop on an error or uncertain outcome. Do not interpret a successful catalog/probe check as successful speech synthesis, an audio playback test, or authorization to make another paid request.
