# Export policy

Each field can have an explicit decision, `Allowed` or `Denied`, and an explicit decision always wins. What happens
to a field **without** a decision depends on the export default:

- **`ApproveFirst`** (built in): the field is **not exported** until someone allows it. This covers new fields,
  renamed fields and fields added in a new template version.
- **`ExportAll`**: the field **is exported** unless someone denies it, so a team can change a template freely and
  its answers flow through without anyone classifying them first.

The default can be set for the whole tenant and overridden for a single template. See
[Export default](#export-default).

## Model

Decisions live in `prism.field_export_policy`, one row per field:

| Column | Meaning |
|---|---|
| `tenant_id`, `template_id` | Decisions are per template, **not** per template version, so they carry over to new versions. |
| `parent_field_id`, `field_id` | The field. `parent_field_id` is the collection field for nested fields, and empty for top-level ones. |
| `decision` | `Allowed` or `Denied`. |
| `policy_version` | The template's policy version when this row last changed. |
| `reason`, `decided_by`, `decided_at` | Audit. |

Defaults live in `prism.export_defaults`, one row per tenant (`template_id` is all zeros) and one per template
that overrides it. `mode` is `ApproveFirst`, `ExportAll`, or `NULL` for inherit. Each row has the same
`policy_version` and audit columns. A template's default comes from its own row if `mode` is set, otherwise from
the tenant row, otherwise it is `ApproveFirst`.

The template's **policy version** is the highest `policy_version` across its decision rows, its own default row
and the tenant default row. Every change raises it, and the version is part of each projection's identity: a
projection made under an older policy is out of date even if the application hasn't changed. A change to the
tenant default takes a version above every template in the tenant, so all of them are re-projected.

| Field state | Facts exported | In `field_catalog` |
|---|---|---|
| No decision, default `ApproveFirst` | No | Yes, `Unclassified` |
| No decision, default `ExportAll` | Yes | Yes, `AllowedByDefault` |
| `Denied` | No | Yes, `Denied` |
| `Allowed` | Yes | Yes, `Allowed` |

A nested field is only exported when **both** the field and its collection are exported. For example, under
`ApproveFirst` allowing `members.memberName` has no effect until `members` is allowed too. Under `ExportAll`,
denying `members` withholds every field inside it.

`field_catalog` always describes every field (label, type, task, page) with no answer values. That's how you see
what still needs classifying.

## Who decides

The data owner for the service classifies the fields, with the product owner. Treat any field that can hold
personal data as `Denied` unless there is an agreed purpose and lawful basis for exporting it. Record the
justification in `reason`.

Switching a tenant or template to `ExportAll` means **every new field is exported as soon as it is published**,
including fields that collect personal data. Agree it with the data owner (and the DPO where personal data is
involved) first, record that agreement in `reason`, and deny the fields that must stay in FlexForms.

## Classifying fields

The admin endpoints need an Entra token for the Prism app registration carrying the **`Prism.Admin`** app role.
Every change is audit-logged with the caller.

### See the fields of a template

```http
GET /api/control/tenants/{tenantId}/templates/{templateId}/export-policy
Authorization: Bearer <token>
```

This returns the policy version, the default that applies (`defaultMode`) and where it comes from
(`defaultSource`: `Template`, `Tenant` or `BuiltIn`), and every catalogued field with its `exportStatus`
(`Unclassified`, `AllowedByDefault`, `Allowed` or `Denied`), its label, type, task and page, and who decided it. A template version is catalogued as soon as
FlexForms publishes it (`TemplateVersionPublishedEvent`), so new fields can be classified before anyone answers them.
Versions published before that event existed are catalogued the first time one of their applications is projected;
until then the endpoint returns 404, so run a backfill for the tenant if needed.

### When a template changes

Every template change is a new template version. When one is published:

1. Prism catalogues it within seconds. Under `ApproveFirst`, new fields are `Unclassified` and are not exported.
   Under `ExportAll` they are `AllowedByDefault` and are exported as applications are projected, so the review
   below is about denying anything that shouldn't leave FlexForms.
2. Review what changed in `prism.v_template_field_changes`:

   ```sql
   SELECT version_number, change_type, parent_field_id, field_id, previous_label, label,
          label_changed, type_changed, required_changed, choices_changed, export_decision
   FROM prism.v_template_field_changes
   WHERE tenant_id = @tenant AND template_id = @template
   ORDER BY version_created_on DESC, change_type, parent_field_id, field_id;
   ```

3. Classify `Added` fields with the `PUT` below.
4. Check `Changed` rows with `label_changed` or `type_changed`. Their existing decision carries over, so set it to
   `Denied` if the field now collects something that shouldn't leave FlexForms.
5. `Removed` fields keep their decision for older versions' applications; nothing needs doing.

### Change decisions

```http
PUT /api/control/tenants/{tenantId}/templates/{templateId}/export-policy
Authorization: Bearer <token>
Content-Type: application/json

{
  "decisions": [
    { "fieldId": "schoolName", "decision": "Allowed", "reason": "Public information" },
    { "fieldId": "members", "decision": "Allowed", "reason": "Collection container only" },
    { "parentFieldId": "members", "fieldId": "role", "decision": "Allowed", "reason": "Not personal" },
    { "parentFieldId": "members", "fieldId": "email", "decision": "Denied", "reason": "Personal data" }
  ]
}
```

- Send only the fields you want to change. Fields you leave out keep their current decision.
- `decision` is required and must be `Allowed` or `Denied`, written as text. Numbers are rejected.
- Every field must already be in the catalogue for this template. A typo returns 422 and lists the unknown fields;
  nothing is saved.
- The response includes `warnings`, for example a nested field that is allowed while its collection is not.
- Repeating decisions that are already in place changes nothing, so the version stays the same and no backfill starts.

When something changes, the policy version goes up by one and **a backfill for the tenant starts automatically**.
The response includes it (`backfill.operationId`). Follow it with `GET /api/control/backfill/{operationId}`.
Until it finishes, the views can still show answers classified under the previous policy:

- **Newly allowed** fields appear as each application is re-projected.
- **Newly denied** fields disappear as each application is re-projected. If a field must disappear
  **immediately**, for example after an incident, also remove its rows by hand, as described in the runbook.

Superseded generations keep their facts until the cleanup retention period passes (30 days by default). They are
not visible through the views, but they are still in the database.

### There is no "undecided"

A decision can't be cleared back to Unclassified through the API, because that would lower the policy version and
stop out-of-date projections from being noticed. Set the field to `Denied` instead.

## Export default

```http
GET /api/control/tenants/{tenantId}/export-default
PUT /api/control/tenants/{tenantId}/export-default
GET /api/control/tenants/{tenantId}/templates/{templateId}/export-default
PUT /api/control/tenants/{tenantId}/templates/{templateId}/export-default
Authorization: Bearer <token>
Content-Type: application/json

{ "mode": "ExportAll", "reason": "Agreed with the data owner on 2026-10-02; personal fields denied" }
```

- `mode` is required: `ExportAll`, `ApproveFirst`, or `Inherit`. `Inherit` clears the setting at that level, so a
  template follows the tenant again and the tenant falls back to `ApproveFirst`. Numbers are rejected.
- `GET` returns `mode` as set at that level, plus `effectiveMode` and `source` once inheritance is resolved.
- Sending the mode and reason already in place changes nothing, so no backfill starts.
- Otherwise the policy version goes up and **a backfill for the tenant starts automatically**, as for decisions.
  Switching to `ExportAll` adds the undecided fields' answers as each application is re-projected; switching back
  to `ApproveFirst` removes them.
- Explicit decisions are untouched by a default change. A field set to `Denied` stays denied under `ExportAll`.

## Checking the result

```sql
-- Fields still waiting for a decision, across every catalogued template version
SELECT DISTINCT c.template_id, c.parent_field_id, c.field_id, c.label
FROM prism.field_catalog c
WHERE c.tenant_id = @tenant AND c.export_status = 'Unclassified';

-- The default in force for each template that has a setting
SELECT d.template_id, d.mode, d.policy_version, d.reason, d.decided_by, d.decided_at
FROM prism.export_defaults d
WHERE d.tenant_id = @tenant;

-- No denied field should ever have facts
SELECT f.parent_field_id, f.field_id, COUNT(*) AS facts
FROM prism.v_current_answer_facts f
INNER JOIN prism.field_export_policy p
  ON p.tenant_id = f.tenant_id AND p.template_id = f.template_id
 AND p.parent_field_id = f.parent_field_id AND p.field_id = f.field_id
WHERE f.tenant_id = @tenant AND p.decision = 'Denied'
GROUP BY f.parent_field_id, f.field_id;
```

The last query should return no rows once the backfill has finished. Under `ApproveFirst`, undecided fields
shouldn't have facts either; `field_catalog.export_status = 'Unclassified'` lists those.
