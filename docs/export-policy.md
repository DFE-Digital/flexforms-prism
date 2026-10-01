# Export policy

Prism only exports answers that someone has explicitly agreed can leave FlexForms. A field with no decision is
**not exported**. That holds for new fields, renamed fields, and fields added in a new template version.

## Model

Decisions live in `prism.field_export_policy`, one row per field:

| Column | Meaning |
|---|---|
| `tenant_id`, `template_id` | Decisions are per template, **not** per template version, so they carry over to new versions. |
| `parent_field_id`, `field_id` | The field. `parent_field_id` is the collection field for nested fields, and empty for top-level ones. |
| `decision` | `Allowed` or `Denied`. |
| `policy_version` | The template's policy version when this row last changed. |
| `reason`, `decided_by`, `decided_at` | Audit. |

The template's **policy version** is the highest `policy_version` across its rows. Every change raises it, and
the version is part of each projection's identity: a projection made under an older policy is out of date even if
the application hasn't changed.

| Field state | Facts exported | In `field_catalog` |
|---|---|---|
| No decision | No | Yes, `Unclassified` |
| `Denied` | No | Yes, `Denied` |
| `Allowed` | Yes | Yes, `Allowed` |

A nested field is only exported when **both** the field and its collection are allowed. For example, allowing
`members.memberName` has no effect until `members` is allowed too.

`field_catalog` always describes every field (label, type, task, page) with no answer values. That's how you see
what still needs classifying.

## Who decides

The data owner for the service classifies the fields, with the product owner. Treat any field that can hold
personal data as `Denied` unless there is an agreed purpose and lawful basis for exporting it. Record the
justification in `reason`.

## Classifying fields

The admin endpoints need an Entra token for the Prism app registration carrying the **`Prism.Admin`** app role.
Every change is audit-logged with the caller.

### See the fields of a template

```http
GET /api/admin/tenants/{tenantId}/templates/{templateId}/export-policy
Authorization: Bearer <token>
```

This returns the policy version and every catalogued field with its `exportStatus` (`Unclassified`, `Allowed`
or `Denied`), its label, type, task and page, and who decided it. A template is catalogued the first time one of its
applications is projected. Before that, the endpoint returns 404; run a backfill for the tenant if needed.

### Change decisions

```http
PUT /api/admin/tenants/{tenantId}/templates/{templateId}/export-policy
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
The response includes it (`backfill.operationId`). Follow it with `GET /api/admin/backfill/{operationId}`.
Until it finishes, the views can still show answers classified under the previous policy:

- **Newly allowed** fields appear as each application is re-projected.
- **Newly denied** fields disappear as each application is re-projected. If a field must disappear
  **immediately**, for example after an incident, also remove its rows by hand, as described in the runbook.

Superseded generations keep their facts until the cleanup retention period passes (30 days by default). They are
not visible through the views, but they are still in the database.

### There is no "undecided"

A decision can't be cleared back to Unclassified through the API, because that would lower the policy version and
stop out-of-date projections from being noticed. Set the field to `Denied` instead.

## Checking the result

```sql
-- Fields still waiting for a decision, for the latest template versions
SELECT DISTINCT c.template_id, c.parent_field_id, c.field_id, c.label
FROM prism.field_catalog c
WHERE c.tenant_id = @tenant AND c.export_status = 'Unclassified';

-- No denied or unclassified field should ever have facts
SELECT f.parent_field_id, f.field_id, COUNT(*) AS facts
FROM prism.v_current_answer_facts f
LEFT JOIN prism.field_export_policy p
  ON p.tenant_id = f.tenant_id AND p.template_id = f.template_id
 AND p.parent_field_id = f.parent_field_id AND p.field_id = f.field_id
WHERE f.tenant_id = @tenant AND (p.decision IS NULL OR p.decision <> 'Allowed')
GROUP BY f.parent_field_id, f.field_id;
```

The second query should return no rows once the backfill has finished.
