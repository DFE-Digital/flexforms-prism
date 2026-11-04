# FlexForms Prism — Agent entry point

Read-side **analytics projection** for FlexForms. It is not the applicant app and not the EA database. FlexForms API publishes thin events; Prism re-reads `/v1/internal/prism` and writes schema `prism` for the data team.

Human docs in `docs/` are authoritative. This file plus `.ai/` tell agents what not to break.

## Ecosystem

Web → API (OLTP + outbox) → Service Bus topic `flexforms-prism` → this Function App → Prism SQL views. API also calls Prism export-policy HTTP.

## Constraints

1. **Events are nudges, never payloads.** Never project from the message body.
2. **Always compare `SourceRevision`.** Older/duplicate messages skip. Deletes are tombstones.
3. **Only Allowed export-policy fields become facts.** Policy changes trigger tenant backfill; views lag until then.
4. **Template authors map field changes** (`retiredFields`, `semanticKey`) in FlexForms, not the data team.
5. Bumping flatten output requires `PrismVersions.ProjectorVersion`. Bumping view contract requires `ContractVersion`. Then reproject.
6. Subscription **must** be session-enabled; topic **must** have duplicate detection — both at **creation**.
7. Permanent failures (`PermanentProjectionException`) go to DLQ with `PrismFailure`. Do not swallow them as complete.
8. Consume `GovUK.Dfe.FlexForms.Api.Client` and CoreLibs **packages**. README “clone sibling API” may be stale vs current `PackageReference`.
9. Excluded tenants (`Prism:ExcludedTenantIds`) skip work — don’t project Playwright/test tenants into the warehouse.
10. Data team reads **views**, not raw generation tables during supersede.

## Knowledge map

| File | Use |
|------|-----|
| [`README.md`](README.md) | Architecture diagram, local run |
| [`docs/prism-contract-v1.md`](docs/prism-contract-v1.md) | Events, internal API, views |
| [`docs/export-policy.md`](docs/export-policy.md) | Classification |
| [`docs/querying-prism.md`](docs/querying-prism.md) | Cross-version reporting |
| [`docs/runbook.md`](docs/runbook.md) | Ops |
| [`docs/azure-setup.md`](docs/azure-setup.md) | Azure resources |
| [`.ai/architecture.md`](.ai/architecture.md) | Code map |
| [`.ai/known-pitfalls.md`](.ai/known-pitfalls.md) | Footguns |
| [`.ai/history-lessons.md`](.ai/history-lessons.md) | Lessons from past design work |
| [`.ai/features/projection.md`](.ai/features/projection.md) | Projector CAS |
| [`.ai/features/flattening.md`](.ai/features/flattening.md) | Flattener |
| [`.ai/features/export-policy.md`](.ai/features/export-policy.md) | Policy API |

## Knowledge maintenance

Read `docs/prism-contract-v1.md` before changing events, internal DTO mapping, or views. Update that contract **and** `.ai/` in the same PR. Do not mark inferred lake-worker designs as current — Prism **is** the projection path. Trust code over docs, and fix the docs when they disagree.

### Definition of done (mandatory)

This is enforced in CI: the **Knowledge base check** job fails a pull request that changes `src/` without touching `docs/`, `README.md`, `AGENTS.md` or `.ai/`, unless the PR has the `no-docs` label. It runs first; build and tests only start once it passes. After adding the label, re-run the failed job.

A task is **not complete** until you have checked whether it changed anything the knowledge base describes. Before finishing:

1. Check the change against the triggers below.
2. Update every affected doc in the same change.
3. In your final summary or PR description, list the docs you updated, **or** state “No knowledge impact” with a one-line reason.

| If you changed… | Update |
|-----------------|--------|
| Event handling, revisions, generations, tombstones | `docs/prism-contract-v1.md`, `.ai/features/projection.md` |
| Flatten output or catalogue | `.ai/features/flattening.md`, golden fixtures, `PrismVersions.ProjectorVersion` |
| SQL views or their columns | `docs/prism-contract-v1.md`, `docs/querying-prism.md`, `ContractVersion` if breaking |
| Export policy behaviour | `docs/export-policy.md`, `.ai/features/export-policy.md` |
| Azure resources, app settings, roles | `docs/azure-setup.md`, `.ai/security.md` |
| Operations, backfill, DLQ handling | `docs/runbook.md` |
| A fix for an incident or footgun | `.ai/known-pitfalls.md` |
| A significant design choice | New file in `.ai/decisions/` |
| Something that makes an existing statement wrong | Correct or delete it — never leave it stale |

This repository is **public**. Never write secrets, connection strings, or descriptions of unfixed security weaknesses into docs.
