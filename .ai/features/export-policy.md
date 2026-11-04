# Feature: Export policy

Docs: `docs/export-policy.md`. Code: `ExportPolicyService`, `ExportPolicyApi`, `Flattener/Policy/ExportPolicy.cs`.

Unclassified fields follow tenant/template default (`ApproveFirst` vs `ExportAll`). Nested collection rules apply. API/Web admin changes policy then Prism backfills.

Never bypass policy in `ProjectionWriter`.
