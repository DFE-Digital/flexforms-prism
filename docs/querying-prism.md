# Querying Prism

A practical guide for data engineers. It explains how answers are laid out in the Prism views and gives worked
queries for the common reporting jobs: one row per application, reporting across template versions, collections,
lookups and one-to-many reports.

[prism-contract-v1.md](prism-contract-v1.md) is the reference for every column; this guide shows how to use them.
The SQL is T-SQL (Azure SQL). Field IDs in the examples (`trustName`, `externalAttendees` and so on) are
illustrative; take the real ones from `prism.field_catalog`.

## Who maps template changes

**The template author does, not the data team.** Once any application uses a template, FlexForms refuses to save
a new version that would break reporting across versions. Each refusal tells the author exactly what to fix. So:

- **Renaming a question:** the author retires the old ID in `retiredFields`, names the new one in `replacedBy`,
  and gives the new question the old reporting key as its `semanticKey`. Answers from both versions come out under
  the same `semantic_key`, and reports keep working with no mapping.
- **Removing, splitting or merging questions:** the author must retire the old question and name its replacements.
  Prism shows them in `replaced_by` in `v_template_field_changes`, so you can see the relationship rather than
  guess it.
- **Changing what a question stores** (text to number, one choice to many, one lookup to another): this isn't
  allowed under the same ID, so a column never silently changes type.
- **Reusing an old ID or reporting key for something else:** this isn't allowed either.

Prism reads what the author declared and publishes it as written. The data team doesn't keep a mapping table. It
only has two jobs:
- approve new or renamed questions for export (see [export-policy.md](export-policy.md));
- decide how to combine questions that were genuinely split or merged, if a report needs them as one (see
  recipe 2).

The authors' rules are in the FlexForms Form Template Designer Manual, section 15.1 "Changing fields once a
template is in use".

## How the data is laid out

Prism stores **one row per answer**, not one row per application. Each row says which application, which question,
which collection item (if any), and the value in a typed column.

| application_id | parent_field_id | field_id | semantic_key | occurrence_path | item_ordinal | nested_path | value_string | value_date |
|---|---|---|---|---|---|---|---|---|
| A1 | | `trustName` | `trustName` | | | | Oak Trust | |
| A1 | | `visitDate` | `visitDate` | | | | | 2026-09-14 |
| A1 | `externalAttendees` | `attendeeName` | `externalAttendees/attendeeName` | `externalAttendees/7f3c…` | 0 | | Sam Jones | |
| A1 | `externalAttendees` | `attendeeName` | `externalAttendees/attendeeName` | `externalAttendees/91ab…` | 1 | | Ali Khan | |
| A1 | | `academySearch` | `academySearch` | | | | Oak Primary | |
| A1 | | `academySearch` | `academySearch` | | | `urn` | 105996 | |

Because the layout is long rather than wide, adding questions, versions or collection items never changes its
shape. You decide the shape of each report in the query.

### Which view to use

| View | Use it for |
|---|---|
| `prism.v_current_answer_facts` | The latest answers of every application, drafts and submitted. `lifecycle` says which. |
| `prism.v_submission_answer_facts` | Answers exactly as submitted, frozen at submission. Use it for "what did they submit" reports. |
| `prism.v_template_field_changes` | What each template version added, removed, renamed or changed. |
| `prism.field_catalog` | Every question of every template version, with label, page, type and export status, but no answers. |

### Rules that apply to every query

- **Always filter on `tenant_id`.**
- **Group on `semantic_key`, not `field_id`, when a report spans template versions.** It's the question's
  permanent reporting name. Template authors keep it when they rename a question, and FlexForms enforces that.
- **Use `nested_path = ''` for "the answer"** to a question. Rows with a `nested_path` are its parts: each ticked
  checkbox, each property of a lookup, each uploaded file.
- **Use the typed column** for the question's type: `value_string`, `value_decimal`, `value_bool`, `value_date`,
  `value_date_time` or `value_json`. `raw_value` is what was stored, before interpretation.
- **Only exported fields appear.** If a question is missing, check its `export_status` in `field_catalog` (see
  [export-policy.md](export-policy.md)).

### A display value

Several recipes need "the answer as text" whatever its type. This expression does that:

```sql
COALESCE(
    f.value_string,
    CONVERT(nvarchar(50), f.value_decimal),
    CASE f.value_bool WHEN 1 THEN 'Yes' WHEN 0 THEN 'No' END,
    CONVERT(nvarchar(10), f.value_date, 23),
    CONVERT(nvarchar(30), f.value_date_time, 126),
    f.value_json,
    f.raw_value) AS answer
```

For a lookup (`nested_path = ''`) `value_string` holds the selected item's `name`. For checkboxes, `value_json`
holds the ticked codes as a JSON array.

## Recipes

### 1. One row per application, one column per question

This answers the data engineer's problem of pivot width growing as more data is collected.

The pivot only grows if its columns come from the answers. Take them from the **catalogue** instead. It lists every
question of every template version as soon as the version is published, before anyone answers it, so the width is
fixed from day one.

A fixed list of columns:

```sql
SELECT
    f.application_id,
    MAX(CASE WHEN f.semantic_key = 'trustName' THEN f.value_string END) AS trust_name,
    MAX(CASE WHEN f.semantic_key = 'visitDate' THEN f.value_date END)   AS visit_date,
    MAX(CASE WHEN f.semantic_key = 'visitType' THEN f.value_string END) AS visit_type
FROM prism.v_current_answer_facts f
WHERE f.tenant_id = @tenant
  AND f.template_id = @template
  AND f.parent_field_id = ''
  AND f.nested_path = ''
GROUP BY f.application_id;
```

Applications that haven't answered a question get `NULL` in that column; the column is always there.

To generate the columns from the catalogue, so new questions appear without editing the query:

```sql
DECLARE @columns nvarchar(max), @sql nvarchar(max);

SELECT @columns = STRING_AGG(CAST(
           'MAX(CASE WHEN f.semantic_key = ' + QUOTENAME(k.semantic_key, '''') + ' THEN f.answer END) AS '
           + QUOTENAME(k.semantic_key) AS nvarchar(max)), ', ')
       WITHIN GROUP (ORDER BY k.first_order)
FROM (
    SELECT c.semantic_key, MIN(c.field_order) AS first_order
    FROM prism.field_catalog c
    WHERE c.tenant_id = @tenant
      AND c.template_id = @template
      AND c.parent_field_id = ''
      AND c.is_collection = 0
      AND c.export_status IN ('Allowed', 'AllowedByDefault')
    GROUP BY c.semantic_key
) k;

SET @sql = N'
SELECT f.application_id, ' + @columns + N'
FROM (
    SELECT application_id, semantic_key,
           COALESCE(value_string, CONVERT(nvarchar(50), value_decimal),
                    CASE value_bool WHEN 1 THEN ''Yes'' WHEN 0 THEN ''No'' END,
                    CONVERT(nvarchar(10), value_date, 23), CONVERT(nvarchar(30), value_date_time, 126),
                    value_json, raw_value) AS answer
    FROM prism.v_current_answer_facts
    WHERE tenant_id = @tenant AND template_id = @template AND parent_field_id = '''' AND nested_path = ''''
) f
GROUP BY f.application_id;';

EXEC sp_executesql @sql, N'@tenant uniqueidentifier, @template uniqueidentifier', @tenant, @template;
```

`QUOTENAME` only accepts up to 128 characters, which covers any sensible question ID.

In Power BI you can skip the pivot altogether: load the long view as the fact table and the catalogue as the
question dimension, and let the matrix visual do the pivot.

### 2. One table across every template version

This covers the data engineer's problem of template changes, such as `name` becoming `firstName` and `lastName`,
and the request for one table instead of one per template version.

Every version is already in the same view, so you don't need a table per `template_version_id`. Recipe 1 already
covers every version, because it filters on `template_id`, not `template_version_id`.

**Renamed questions.** When an author renames `trustName` to `incomingTrustName`, FlexForms makes them keep
`semantic_key = 'trustName'`. Answers from both versions come out under the same key, so the column in recipe 1
keeps working with no mapping.

**Split or merged questions.** If `name` becomes `firstName` and `lastName`, those are genuinely different
questions with new keys. The author must declare the split, so you can see it rather than guess:

```sql
SELECT version_number, field_id, replaced_by
FROM prism.v_template_field_changes
WHERE tenant_id = @tenant AND template_id = @template AND change_type = 'Removed';
-- version 2.0 | name | firstName, lastName
```

How to combine them in a report is still your choice, for example:

```sql
COALESCE(
    MAX(CASE WHEN f.semantic_key = 'name' THEN f.value_string END),
    NULLIF(CONCAT_WS(' ',
        MAX(CASE WHEN f.semantic_key = 'firstName' THEN f.value_string END),
        MAX(CASE WHEN f.semantic_key = 'lastName' THEN f.value_string END)), '')) AS full_name
```

**Before the post-deployment backfill.** Catalogues written before semantic keys existed have `semantic_key` set
to `NULL` until the Prism admin runs a backfill. If you query before then, use this instead of `semantic_key`.
It gives the same key for any question that has never been renamed.

```sql
COALESCE(f.semantic_key,
         CASE WHEN f.parent_field_id = '' THEN f.field_id ELSE f.parent_field_id + '/' + f.field_id END) AS reporting_key
```

### 3. Several answers in one cell

This is the data engineer's grouping problem, for example External Attendees.

Each collection item is its own set of rows. `item_ordinal` is the order the user sees them in.

```sql
SELECT
    f.application_id,
    STRING_AGG(CAST(f.value_string AS nvarchar(max)), ', ')
        WITHIN GROUP (ORDER BY f.item_ordinal, f.item_id) AS external_attendees
FROM prism.v_current_answer_facts f
WHERE f.tenant_id = @tenant
  AND f.template_id = @template
  AND f.semantic_key = 'externalAttendees/attendeeName'
  AND f.nested_path = ''
GROUP BY f.application_id;
```

Ticked checkboxes work the same way. Each ticked option is a row whose `nested_path` is the option's code:

```sql
SELECT f.application_id,
       STRING_AGG(CAST(f.value_string AS nvarchar(max)), ', ') WITHIN GROUP (ORDER BY f.value_string) AS support_types
FROM prism.v_current_answer_facts f
WHERE f.tenant_id = @tenant AND f.semantic_key = 'supportTypes' AND f.nested_path <> ''
GROUP BY f.application_id;
```

To show the option labels rather than codes, join to `field_catalog.choices_json`, or use `value_json` on the
`nested_path = ''` row, which holds all ticked codes as a JSON array.

`item_ordinal` is empty for derived collections. Order by `item_id` there.

### 4. One value taken from several questions

This is the data engineer's problem of the organisation name being pulled from 7 fields.

Prism keeps each question separate, so choosing between them is a single `COALESCE` in priority order:

```sql
WITH answers AS (
    SELECT f.application_id, f.semantic_key, f.value_string
    FROM prism.v_current_answer_facts f
    WHERE f.tenant_id = @tenant AND f.template_id = @template AND f.parent_field_id = '' AND f.nested_path = ''
      AND f.semantic_key IN ('trustSearch', 'academySearch', 'localAuthority', 'otherOrganisationName')
)
SELECT application_id,
       COALESCE(
           MAX(CASE WHEN semantic_key = 'trustSearch' THEN value_string END),
           MAX(CASE WHEN semantic_key = 'academySearch' THEN value_string END),
           MAX(CASE WHEN semantic_key = 'localAuthority' THEN value_string END),
           MAX(CASE WHEN semantic_key = 'otherOrganisationName' THEN value_string END)) AS organisation_name
FROM answers
GROUP BY application_id;
```

For lookups, `value_string` on the `nested_path = ''` row is already the selected organisation's name.

### 5. Values inside a lookup

This is the data engineer's academy-search example, where the answer is a JSON object such as
`{"name": "...", "ukprn": "...", "urn": "...", ...}`.

You don't need to parse JSON. Prism writes the lookup three ways:

| nested_path | What's in it |
|---|---|
| `''` | `value_string` = the `name`; `value_json` = the whole object |
| `name`, `ukprn`, `urn`, `localAuthorityName`, `gor`, `postcode`, … | One row per property, in `value_string` (or `value_decimal` / `value_bool`) |

```sql
SELECT
    f.application_id,
    MAX(CASE WHEN f.nested_path = '' THEN f.value_string END)                   AS academy_name,
    MAX(CASE WHEN f.nested_path = 'urn' THEN f.value_string END)                AS urn,
    MAX(CASE WHEN f.nested_path = 'ukprn' THEN f.value_string END)              AS ukprn,
    MAX(CASE WHEN f.nested_path = 'localAuthorityName' THEN f.value_string END) AS local_authority
FROM prism.v_current_answer_facts f
WHERE f.tenant_id = @tenant AND f.semantic_key = 'academySearch'
GROUP BY f.application_id;
```

If you prefer the JSON, `JSON_VALUE(f.value_json, '$.urn')` on the `nested_path = ''` row works too.

A lookup that allows several selections has no `nested_path = ''` row. Each selection is numbered instead:
`#0` holds the first selection's name and JSON, `#0/urn` its URN, then `#1`, `#1/urn` and so on. To list the
names, filter on `f.nested_path LIKE '#%' AND f.nested_path NOT LIKE '#%/%'`.

The text a user typed into the search box isn't stored as an answer, only what they selected. Keys like
`Data[mpSearch-field-flow_query]` don't exist in Prism.

### 6. One-to-many: a child table per collection

This is the data engineer's problem of storing array values with record numbers.

Collections already have record identity. `item_id` is the item's stable ID and `item_ordinal` is its position.
Build a child table keyed on `(application_id, item_id)` and relate it to the application table, so Power BI handles
the one-to-many.

```sql
SELECT
    f.application_id,
    f.item_id,
    MIN(f.item_ordinal) AS record_no,
    MAX(CASE WHEN f.semantic_key = 'externalAttendees/attendeeName' THEN f.value_string END) AS attendee_name,
    MAX(CASE WHEN f.semantic_key = 'externalAttendees/organisation' THEN f.value_string END) AS organisation,
    MAX(CASE WHEN f.semantic_key = 'externalAttendees/role' THEN f.value_string END)         AS role
FROM prism.v_current_answer_facts f
WHERE f.tenant_id = @tenant
  AND f.template_id = @template
  AND f.parent_field_id = 'externalAttendees'
  AND f.nested_path = ''
GROUP BY f.application_id, f.item_id;
```

Use `item_ordinal` for display order only, because it changes when a user reorders or removes items. Use `item_id`
as the key.

If the collection is renamed in a later template version, its `parent_field_id` changes but its `semantic_key`
prefix doesn't. Filter on `f.semantic_key LIKE 'externalAttendees/%'` to cover every version.

### 7. The MP example, rewritten

Here is the data engineer's query against Prism. The selected MP is a lookup in a collection, so each visit's MP is
one row with `nested_path = ''`:

```sql
SELECT
    f.application_id,
    STRING_AGG(CAST(f.value_string AS nvarchar(max)), CHAR(13) + CHAR(10))
        WITHIN GROUP (ORDER BY f.item_ordinal, f.item_id) AS mp_details
FROM prism.v_current_answer_facts f
WHERE f.tenant_id = @tenant
  AND f.template_id = @template
  AND f.field_id = 'mpSearch'
  AND f.nested_path = ''
GROUP BY f.application_id;
```

You don't need a config table to add a new template. Every template of the tenant is already in the same views,
so a new one appears as soon as its applications are saved.

## Reviewing template changes

After a template version is published, check what changed before relying on a report:

```sql
SELECT version_number, change_type, semantic_key, parent_field_id, field_id, previous_field_id,
       previous_label, label, field_id_changed, label_changed, type_changed, replaced_by, export_decision
FROM prism.v_template_field_changes
WHERE tenant_id = @tenant AND template_id = @template
ORDER BY version_created_on DESC, change_type, semantic_key;
```

| What you see | What it means for your reports |
|---|---|
| `Changed` with `field_id_changed = 1` | Renamed. Reports on `semantic_key` keep working. The new ID needs an export decision before its answers appear. |
| `Changed` with `label_changed = 1` | Same question, new wording. Check that the meaning hasn't changed. |
| `Added` | A new question, which appears in the catalogue straight away. Add it to reports if you need it. |
| `Removed` with `replaced_by` | Retired by the author, with its replacement named. Older applications still have their answers. |

## Loading into another platform

- **Full reload:** the simplest approach, and fine at current volumes.
- **Incremental:** pick up applications whose `projected_at` is later than your last load, then delete and replace
  **all** their rows. A new `generation_id` for an application replaces every previous row, not just the changed
  ones. Applications that disappear from the view have been deleted, so remove them too.
- `v_submission_answer_facts` rows never change once written, so they can be appended by `submission_id`.

## Common gotchas

| Symptom | Cause |
|---|---|
| A question has no rows at all | It isn't exported yet. Check `field_catalog.export_status`. |
| A value is in `raw_value` but not the typed column | `interpretation_status` is `ParseFailed`: the user typed something that isn't a valid date or number. |
| `STRING_AGG` fails with a length error | Cast the value to `nvarchar(max)` first, as in the recipes. |
| The same question shows up twice in a pivot | You've grouped on `field_id` across a rename. Group on `semantic_key`. |
| A checkbox question has one row more than it has ticks | The `nested_path = ''` row is the selection as a whole; filter it out when counting ticks. |
| Drafts appear in a report | `v_current_answer_facts` includes drafts. Filter on `lifecycle = 'Submitted'`, or use `v_submission_answer_facts`. |
