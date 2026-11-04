# Domain model (Prism)

Reporting concepts, not EA aggregates.

| Concept | Meaning | Code |
|---------|---------|------|
| Generation | Immutable flattened snapshot (Current or Submission) | `ProjectionGeneration` |
| Application projection state | Pointer to latest current generation | `ApplicationProjectionState` |
| Answer fact | One typed cell | `AnswerFact` / `AnswerFactEntity` |
| Submission snapshot | Frozen submit generation | `SubmissionSnapshot` |
| Field catalogue | Template field metadata | `FieldCatalogEntry` |
| Export policy | Allowed/Denied | `FieldExportPolicy` |
| Export default | ApproveFirst vs ExportAll | `ExportDefault` |
| Deletion tombstone | Prevents resurrecting deleted apps | `DeletionTombstone` |
| Backfill operation | Async resync via same topic | `BackfillOperation` |

Versions: `Flattener/PrismVersions.cs` (`ProjectorVersion`, `ContractVersion`).

Views (data team): `Migrations/PrismViews.cs` — `v_applications`, `v_current_answer_facts`, `v_submission_answer_facts`, `v_template_field_changes`. `v_applications` includes template version number, template name, tenant name (**Verified** — migration `AddTemplateAndTenantToApplicationsView`).
