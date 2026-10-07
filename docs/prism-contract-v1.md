# Prism contract, version 1

Prism has three contracts. Each one can change without breaking the other two:

1. **The events** that FlexForms publishes when an application changes or a template version is published.
2. **The internal read API** that Prism calls to fetch the current source state.
3. **The SQL views** that the data team reads.

The C# types for the first two live in `GovUK.Dfe.CoreLibs.Messaging.Contracts` and
`GovUK.Dfe.CoreLibs.Contracts`. They are the source of truth; this document explains how they are meant to be used.

## 1. The event

`ApplicationProjectionRequestedEvent` (`GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events`).

| Field | Type | Rules |
|---|---|---|
| `ContractVersion` | int | `1`. Prism dead-letters any other value with reason `unsupported_contract`. |
| `TenantId` | Guid | Always required. |
| `ApplicationId` | Guid | Always required. |
| `Reason` | `Saved`, `Submitted`, `Deleted`, `Resync` | What happened. `Resync` is only sent by Prism's own backfill and reconciliation. |
| `SourceRevision` | long | The **application-level** revision. FlexForms increments it on every save, submit and delete, in the same transaction as the change. |
| `ResponseId` | Guid? | Required for `Saved` and `Submitted`. |
| `SubmissionId` | Guid? | Required for `Submitted`. |
| `TemplateId`, `TemplateVersionId` | Guid? | Sent when known. |
| `OperationId` | Guid? | Only on `Resync`: the backfill or reconciliation operation. |
| `OccurredAt` | DateTime (UTC) | When the transition happened. Used for the lag metric. |

### Transport

- **Topic:** `flexforms-prism`. **Subscription:** `prism-projector`, with sessions enabled.
- **Session ID:** `{tenantId}:{applicationId}`, so the messages for one application are handled one at a time, in order.
- **MessageId:** a UUIDv5 under namespace `6f1c2a4e-8d3b-4f57-9a0e-2b7c5d9e1f30` of the name
  `prism:{tenantId}:{applicationId}:{sourceRevision}:{reason}`. For `Resync`, `:{operationId}` is appended so a
  later backfill of the same revision isn't dropped as a duplicate. The topic's duplicate detection discards
  outbox redeliveries.
- **Body:** a MassTransit JSON envelope (`application/vnd.masstransit+json`), with the event in `message`.
- **Publishing:** FlexForms always publishes this event through the transactional outbox, inside the
  transaction that changes the data and increments `SourceRevision`. If the transaction rolls back, there is no event.

Use `ApplicationProjectionIdentifiers` for the session ID, message ID and submission ID. Never derive them by hand.

### Meaning

The event is a **notification, not a payload**. Prism never projects from the event body. It reads the current
state from the API and compares revisions. As a result:

- Duplicates, reordering and replays are harmless. A message whose revision is at or below what is already
  projected is skipped.
- Coalescing is expected. When revisions 10 and 11 are queued, the first message already sees revision 11 and
  the second is skipped.
- Deletes are final. Once a delete is recorded, a tombstone stops any later or stale message from bringing the
  application back.
- `SubmissionId` is `UUIDv5("submission:{applicationId}:{submittedRevision}")`. Applications are submitted once
  and never reopened.

### Failure handling

| Outcome | What Prism does |
|---|---|
| Projected or skipped | Completes the message. |
| Permanent failure | Dead-letters immediately with reason `unsupported_contract`, `invalid_message`, `source_not_found`, `source_rejected`, `source_mismatch`, `unreadable_template` or `unreadable_response`. The reason is also in the `PrismFailure` application property. |
| Anything else (SQL, network, API 5xx or 429) | Abandons the message, so Service Bus redelivers it until `MaxDeliveryCount`, then dead-letters it. |

### Template versions

`TemplateVersionPublishedEvent` (same namespace) tells Prism that a new template version exists, so its fields are
catalogued, and can be classified, before any application uses it.

| Field | Type | Rules |
|---|---|---|
| `ContractVersion` | int | `1`. Any other value is dead-lettered with `unsupported_contract`. |
| `TenantId`, `TemplateId`, `TemplateVersionId` | Guid | Always required. |
| `VersionNumber` | string | The version label, for example `1.4.0`. Informational. |
| `CreatedAt` | DateTime (UTC) | When the version was created. |

- **When:** FlexForms publishes it when a template is created with an initial version, and every time a new version
  is added. Template versions are immutable, so there is no "changed" or "deleted" event: a change to a template
  is always a new version.
- **Transport:** the same topic and subscription. The envelope's `messageType` is
  `urn:message:GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events:TemplateVersionPublishedEvent`; Prism uses it to
  tell the two events apart. **Session ID:** `{tenantId}:template:{templateId}`. **MessageId:** UUIDv5 of
  `prism-template-version:{tenantId}:{templateVersionId}`. Both come from `ApplicationProjectionIdentifiers`.
- **Publishing:** through the transactional outbox, in the transaction that inserts the version row.
- **Meaning:** a notification. Prism fetches the version from `GET template-versions/{templateVersionId}`, checks it
  belongs to `TemplateId` (otherwise `source_mismatch`), and catalogues it into `field_catalog` and
  `template_versions` with the template's current export policy. Redelivery is harmless. Failures are handled
  exactly like the application event.

Versions created before FlexForms started publishing this event are catalogued the first time one of their
applications is projected, as before.

## 2. Internal read API (FlexForms)

Base path `v1/internal/prism`. Every call needs an Entra token for the FlexForms API's app registration
(`PlatformBearer`) carrying the app role **`Prism.Read`**. Every endpoint except `tenants` also needs an
`X-Tenant-ID` header.

| Endpoint | Returns |
|---|---|
| `GET tenants` | `PrismTenantDto[]`: the authoritative tenant list, from tenant configuration. |
| `GET applications/{applicationId}/current` | `PrismApplicationStateDto`: revision, status, deleted flag, template version, latest response with its body, and the submitted revision, submission ID and submitted response ID. |
| `GET responses/{responseId}` | `PrismResponseDto`: one immutable response version. |
| `GET template-versions/{templateVersionId}` | `PrismTemplateVersionDto`: the template ID, version number, template JSON and creation time. Used to flatten answers and to catalogue published versions; the creation time orders versions in `v_template_field_changes`. Template versions are immutable, so Prism caches them for as long as the process runs. |
| `GET applications?modifiedSince=&page=&pageSize=` | `PrismApplicationPageDto` of `PrismApplicationSummaryDto`, ordered by creation. **Deleted applications are included**, with `IsDeleted = true`. |

Applications are soft-deleted only. If FlexForms ever hard-deletes, it will need a deletion ledger first;
otherwise reconciliation can't find lost deletes.

## 3. SQL views (for consumers)

Read **only** the views in schema `prism`. The tables and their columns may change between projector versions.

### `prism.v_applications`

One row per application that isn't deleted, with what isn't an answer. Join it to the fact views on
`(tenant_id, application_id)`.

| Column | Meaning |
|---|---|
| `tenant_id`, `application_id` | Identity. Always filter on `tenant_id`. |
| `application_reference` | The application reference users see in FlexForms. |
| `lifecycle` | `Draft` or `Submitted`. Deleted applications never appear. |
| `template_id`, `template_version_id` | The template version of the latest response. |
| `created_on` | When the application was created in FlexForms (UTC). |
| `last_modified_on` | When it was last changed in FlexForms (UTC); `created_on` if it never was. |
| `last_submitted_at` | When it was last submitted, from the submission snapshots. Null if it never was. |
| `source_revision`, `response_id`, `projected_at` | As in `v_current_answer_facts`. |

`application_reference`, `created_on` and `last_modified_on` are null for an application until it is projected at
projector version 2 or later; after deploying, nightly reconciliation does that, or run a backfill.
Who created the application isn't published.

### `prism.v_current_answer_facts`

The latest projected answers of every application that isn't deleted. There is one row per fact.

| Column | Meaning |
|---|---|
| `tenant_id`, `application_id` | Identity. Always filter on `tenant_id`. |
| `lifecycle` | `Draft` or `Submitted`. Deleted applications never appear. |
| `source_revision` | The FlexForms revision these facts reflect. |
| `response_id`, `template_id`, `template_version_id` | The source of the answers. |
| `projected_at` | When Prism last wrote the state row. |
| `generation_id` | Changes whenever the facts change. Use it for incremental loads: a new `generation_id` for an application replaces **all** of its previous rows. |
| `projector_version`, `contract_version`, `export_policy_version` | How the facts were produced. |
| fact columns | See below. |

### `prism.v_submission_answer_facts`

The answers **exactly as submitted**, frozen at submission. It has the same fact columns, keyed by
`submission_id`, with `response_id`, `response_revision`, `source_revision` and `submitted_at`. Later changes
never alter these rows. They disappear only if the application is deleted.

### Fact columns

| Column | Meaning |
|---|---|
| `field_id`, `parent_field_id` | The template field. `parent_field_id` is the collection field for nested fields, and empty for top-level ones. |
| `occurrence_path` | Which collection item the fact belongs to. It's empty for top-level fields. Otherwise it alternates collection field IDs and item IDs, separated by `/`, for example `members/4d69b42b-...`. Inside a segment, `%`, `/` and `#` are percent-encoded. An item with no ID gets a synthetic segment `#{ordinal}`. |
| `item_id`, `item_ordinal` | The item's own ID, and its position for display. Don't use the position as an identifier. |
| `nested_path` | Distinguishes the parts of one answer: each checkbox code, each autocomplete property, each uploaded file. Empty for simple fields. |
| `data_type` | `string`, `number`, `DateTime`, `boolean` or `array`. |
| `is_completed` | The completed flag stored with the answer. |
| `interpretation_status` | `Ok`, `Null`, `Empty`, `ParseFailed` (only `raw_value` is reliable) or `Unsupported` (the value is in `value_json`). |
| `value_string`, `value_decimal`, `value_bool`, `value_date`, `value_date_time`, `value_json` | The typed value. Only the column for the interpreted type is set. |
| `raw_value` | The stored value before interpretation, HTML-decoded. |
| `label`, `task_group_*`, `task_*`, `page_*` | Field metadata from `field_catalog`. |
| `semantic_key` | The field's reporting key across template versions, from `field_catalog`. It's the template author's `semanticKey`, or the field ID when there is none. Nested fields are prefixed with their collection's key and a `/`, for example `attendees/attendeeName`. When an author renames a field they keep its `semanticKey`, so group or pivot on `semantic_key` rather than `field_id` to report across versions. FlexForms refuses template versions that change a field's key, reuse a dropped key, or let a new field take over a key without declaring it as a replacement. |

Within one generation, `(field_id, parent_field_id, occurrence_path, nested_path)` is unique.

### `prism.v_template_field_changes`

What each template version changed compared with the version created before it, so the data team can review new,
removed and altered fields. There is one row per field that changed. Every field of a template's first catalogued
version appears as `Added`. Fields are matched by `semantic_key`, so a field the author renamed while keeping its
key is one `Changed` row with `field_id_changed` set. A field retired without a successor that keeps its key shows as
`Removed`, with the author's declared replacements in `replaced_by`.

| Column | Meaning |
|---|---|
| `tenant_id`, `template_id`, `template_version_id`, `version_number`, `version_created_on` | The version. Always filter on `tenant_id`. |
| `previous_template_version_id`, `previous_version_number` | The version it is compared with, by creation time. Null for the first version. |
| `parent_field_id`, `field_id` | The field: its IDs in this version, or in the previous version for `Removed` rows. |
| `previous_parent_field_id`, `previous_field_id` | The field's IDs in the previous version. Null for `Added` rows. |
| `semantic_key` | The reporting key the two versions were matched on. |
| `change_type` | `Added`, `Removed` or `Changed`. |
| `field_id_changed`, `label_changed`, `type_changed`, `required_changed`, `choices_changed`, `location_changed` | For `Changed` rows, what changed. `field_id_changed` means the field (or its collection) was renamed. `type_changed` covers data type, control type and collection; `location_changed` covers task, page and flow. IDs, labels and choices are compared case-sensitively. |
| `replaced_by` | For `Removed` rows, the replacing field IDs the author declared in the version's `retiredFields`, comma separated. Null when none were declared. |
| `previous_label`, `label`, `previous_data_type`, `data_type`, `previous_control_type`, `control_type`, `previous_is_required`, `is_required`, `previous_choices_json`, `choices_json`, `previous_task_name`, `task_name`, `previous_page_title`, `page_title` | Before and after. |
| `export_decision` | The field's current decision for the template: `Allowed`, `Denied`, or for an undecided field `AllowedByDefault` (export default `ExportAll`) or `Unclassified`. |
| `contract_version` | The catalogue contract version compared. Only the current one is shown. |

Under the built-in `ApproveFirst` default a new field is `Unclassified` until someone decides, so it is never
exported by accident. A tenant or template set to `ExportAll` exports it straight away as `AllowedByDefault`. A
relabelled field keeps its decision, because decisions are per field ID; review `label_changed` rows in case the
meaning changed. A renamed field has a new ID, so it needs its own decision; review `field_id_changed` rows.

Catalogues written before semantic keys were recorded have no `semantic_key`; the view falls back to the key the
field ID implies, which is correct for every field without an authored `semanticKey`. Projector version 2 records
them, so after deploying it nightly reconciliation (or a backfill) re-projects each application, which re-catalogues
its template version with authored keys and retirements.

### Guarantees

- **Atomic generations.** A generation's facts become visible in one transaction. Readers never see a partly written
  generation, and never see facts from two generations of the same application at the same time.
- **Fails closed by default.** Only `Allowed` fields appear, plus undecided fields where the tenant or template
  opted into `ExportAll`; `Denied` fields never appear (see [export-policy.md](export-policy.md)).
  `field_catalog` lists every field, including unclassified and denied ones, without values. New template versions
  are catalogued when published, so their new fields show up there (and in `v_template_field_changes`) before any
  answers exist.
- **Eventual consistency.** The views follow FlexForms within seconds normally. Reconciliation repairs any drift
  every night.

## Versioning rules

- **`ContractVersion`** changes only for breaking changes to the events, the internal API or the meaning of the views.
  Additive changes don't bump it. Each event carries its own `ContractVersion` (`CurrentContractVersion` on its type). When it does change, Prism has to accept both versions before FlexForms
  starts publishing the new one.
- **`PrismVersions.ProjectorVersion`** is bumped whenever flattening output changes for the same input. Prism then
  re-projects every application at its current revision (run a backfill; see the runbook).
- **`export_policy_version`** rises with every export policy change for a template.
