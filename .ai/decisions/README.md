# Decisions (Prism)

No `docs/adrs/` in this repo. Binding decisions:

1. **Event = notification** — `docs/prism-contract-v1.md`. Captured on API as `.ai/decisions/prism-events-as-nudges.md`.
2. **Long facts** — mitigates JSON-template ADR reporting pain.
3. **Export policy in Prism** — classification is a reporting concern; FlexForms API proxies admin UI.
4. **Sessions per application** — ordering without blocking other apps.

Original alternatives (lake flatten on HTTP thread, fat payloads, EventMappings ingest) were rejected in the data-pipeline review; see `history-lessons.md`.
