# Business rules (Prism)

- Applications pin a template version; facts are versioned by `template_version_id` + `field_id`.
- Submit is once; `SubmissionId` is deterministic. Submitted facts freeze the **submitted response id**, not later saves.
- Deletes are final (tombstone).
- Only export-policy **Allowed** fields (and allowed collections) become facts.
- Template field identity is immutable; labels may change; removals use `retiredFields`.
- Backfill/reconciliation only enqueue `Resync`; they never write facts themselves.
