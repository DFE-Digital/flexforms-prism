# Building Power BI reports from Prism

This guide is for Power BI users who want to build their own reports from FlexForms data. You don't need to know
how FlexForms or Prism work, and you only need to copy and paste the SQL in this guide. Each tutorial builds on the
one before, so work through them in order the first time.

If you're comfortable with SQL and want more query examples, see [querying-prism.md](querying-prism.md).

- [Before you start](#before-you-start)
- [How the data is shaped](#how-the-data-is-shaped)
- [Tutorial 1: Connect and build your first report](#tutorial-1-connect-and-build-your-first-report)
- [Tutorial 2: Load the answers and questions](#tutorial-2-load-the-answers-and-questions)
- [Tutorial 3: Chart the answers to one question](#tutorial-3-chart-the-answers-to-one-question)
- [Tutorial 4: One row per application, one column per question (matrix)](#tutorial-4-one-row-per-application-one-column-per-question-matrix)
- [Tutorial 5: A flat wide table you can filter and export](#tutorial-5-a-flat-wide-table-you-can-filter-and-export)
- [Tutorial 6: Checkbox questions](#tutorial-6-checkbox-questions)
- [Tutorial 7: Lookup questions (schools, trusts, MPs)](#tutorial-7-lookup-questions-schools-trusts-mps)
- [Tutorial 8: Repeating sections (attendees, organisations)](#tutorial-8-repeating-sections-attendees-organisations)
- [Tutorial 9: Submitted applications only, and what was submitted](#tutorial-9-submitted-applications-only-and-what-was-submitted)
- [Tutorial 10: Keep an eye on template changes](#tutorial-10-keep-an-eye-on-template-changes)
- [Publish and refresh](#publish-and-refresh)
- [Troubleshooting](#troubleshooting)

## Before you start

Ask the Prism admin for:

| You need | What it looks like |
|---|---|
| Access | Your account (or a group you're in) added to the `prism_reader` role. |
| Server | `something.database.windows.net` |
| Database | The Prism database name. |
| Tenant ID | A long ID like `3f2b7c1e-0000-0000-0000-000000000000`. It identifies your organisation's data. |
| Template ID | The same kind of ID, one per form (for example RG Visits). |

In every SQL statement in this guide, replace `<tenant-id>` and `<template-id>` with your IDs, keeping the
single quotes around them.

You sign in with your normal Microsoft work account. There is no separate password.

## How the data is shaped

Prism gives you five views. Think of them as ready-made tables.

| View | What's in it | One row per |
|---|---|---|
| `prism.v_applications` | Reference, status, created, last modified and last submitted dates. | Application |
| `prism.v_current_answer_facts` | The latest answers, for drafts and submitted applications. | Answer |
| `prism.v_submission_answer_facts` | The answers exactly as they were when submitted. They never change afterwards. | Answer in a submission |
| `prism.field_catalog` | Every question of every form version: its wording, page and type. No answers. | Question in a form version |
| `prism.v_template_field_changes` | What each new form version added, removed, renamed or reworded. | Change |

The answer views are **long**, not wide. Instead of one row per application with a column per question, they have
one row per answer:

| application | question | answer |
|---|---|---|
| APP-1 | Trust name | Oak Trust |
| APP-1 | Visit date | 14/09/2026 |
| APP-2 | Trust name | Elm Trust |

That's deliberate. When the form gets new questions, the views don't change shape, so your report doesn't break.
Power BI turns long data into a grid for you (tutorials 4 and 5).

Each question has a **question key**. It stays the same when the form author renames or rewords the question in a
later version, so a report built on the question key keeps working across form versions. You don't have to map
anything; the form author does that when they change the form.

Only questions that have been approved for export appear. If a question is missing, see
[Troubleshooting](#troubleshooting).

## Tutorial 1: Connect and build your first report

**Goal:** a chart of how many applications were started each month, split by draft and submitted.

### Connect

1. Open Power BI Desktop and select **Get data** > **Azure SQL database**. If it isn't listed, choose **More...**
   and search for it.
2. Enter the **Server** and **Database** from the admin.
3. Choose **Import**. (DirectQuery works too but is slower for these views.)
4. Expand **Advanced options**, and paste this into **SQL statement**:

   ```sql
   SELECT *
   FROM prism.v_applications
   WHERE tenant_id = '<tenant-id>'
     AND template_id = '<template-id>'
   ```

5. Select **OK**. When asked how to sign in, choose **Microsoft account**, select **Sign in**, then **Connect**.
6. In the preview, select **Transform Data**.
7. On the right, under **Query settings**, rename the query from `Query1` to `Applications`.
8. Select **Close & Apply**.

### Build the chart

1. In the **Visualizations** pane, select **Stacked column chart**.
2. From the `Applications` table, drag `created_on` to the **X-axis**. Power BI creates a date hierarchy; remove
   **Quarter** and **Day** so you're left with **Year** and **Month**.
3. Drag `application_id` to the **Y-axis**. Select the arrow next to it and choose **Count (Distinct)**.
4. Drag `lifecycle` to **Legend**.

That's it: started applications per month, drafts and submitted shown separately.

`created_on` is empty for applications Prism hasn't refreshed yet, which only happens for a short time after a
Prism upgrade.

## Tutorial 2: Load the answers and questions

**Goal:** add the answers and the list of questions, and link them to the applications. Every later tutorial uses
these three tables.

### The Answers table

1. Select **Home** > **Get data** > **Azure SQL database**, enter the same server and database, choose **Import**,
   and paste this into **SQL statement**:

   ```sql
   SELECT
       f.application_id,
       COALESCE(f.semantic_key, f.field_id) AS question_key,
       f.lifecycle,
       f.value_decimal AS number_answer,
       COALESCE(f.value_date, CAST(f.value_date_time AS date)) AS date_answer,
       COALESCE(
           ticks.answer_text,
           choice.label,
           f.value_string,
           CONVERT(nvarchar(50), f.value_decimal),
           CASE f.value_bool WHEN 1 THEN 'Yes' WHEN 0 THEN 'No' END,
           CONVERT(nvarchar(10), f.value_date, 103),
           CONVERT(nvarchar(20), f.value_date_time, 103),
           f.raw_value) AS answer_text
   FROM prism.v_current_answer_facts f
   LEFT JOIN prism.field_catalog c
       ON c.tenant_id = f.tenant_id
      AND c.template_version_id = f.template_version_id
      AND c.parent_field_id = f.parent_field_id
      AND c.field_id = f.field_id
      AND c.contract_version = f.contract_version
   OUTER APPLY (
       SELECT TOP 1 o.label
       FROM OPENJSON(c.choices_json) WITH (value nvarchar(400) '$.value', label nvarchar(400) '$.label') o
       WHERE o.value = f.value_string) choice
   OUTER APPLY (
       SELECT STRING_AGG(CAST(COALESCE(o.label, s.value) AS nvarchar(max)), ', ') AS answer_text
       FROM OPENJSON(CASE WHEN LOWER(c.control_type) = 'checkboxes' THEN f.value_json END) s
       LEFT JOIN OPENJSON(c.choices_json) WITH (value nvarchar(400) '$.value', label nvarchar(400) '$.label') o
           ON o.value = s.value) ticks
   WHERE f.tenant_id = '<tenant-id>'
     AND f.template_id = '<template-id>'
     AND f.parent_field_id = ''
     AND f.nested_path = ''
   ```

2. Select **Transform Data**, rename the query to `Answers`, and select **Close & Apply**.

You don't need to understand the SQL. It takes every top-level answer and works out a readable `answer_text`:
- radio buttons and drop-downs show the option's wording rather than its code;
- checkboxes show every ticked option, separated by commas;
- dates are shown as dd/mm/yyyy, and yes/no questions as Yes or No.

Numbers and dates are also in `number_answer` and `date_answer`, so you can add them up or plot them over time.
Answers inside repeating sections are left out here; tutorial 8 covers them.

### The Questions table

1. Do the same again with this SQL statement, and name the query `Questions`:

   ```sql
   SELECT question_key, question, page, section, answer_type, question_order
   FROM (
       SELECT
           COALESCE(c.semantic_key, c.field_id) AS question_key,
           c.label AS question,
           c.page_title AS page,
           c.task_name AS section,
           c.data_type AS answer_type,
           c.field_order AS question_order,
           ROW_NUMBER() OVER (PARTITION BY COALESCE(c.semantic_key, c.field_id) ORDER BY c.created_at DESC) AS newest
       FROM prism.field_catalog c
       WHERE c.tenant_id = '<tenant-id>'
         AND c.template_id = '<template-id>'
         AND c.parent_field_id = ''
         AND c.is_collection = 0
         AND c.export_status IN ('Allowed', 'AllowedByDefault')
         AND c.contract_version = (SELECT MAX(contract_version) FROM prism.field_catalog WHERE tenant_id = '<tenant-id>')
   ) q
   WHERE newest = 1
   ```

This has one row per question, with the wording from the newest form version. It includes questions nobody has
answered yet, so your report columns are there from day one.

### Link the tables

1. Select the **Model view** icon on the left.
2. Drag `application_id` from `Applications` onto `application_id` in `Answers`. Check that the line shows **1** on
   the `Applications` side and **\*** (many) on the `Answers` side.
3. Drag `question_key` from `Questions` onto `question_key` in `Answers`. Again, **1** on `Questions`, **\*** on
   `Answers`.

If Power BI has already drawn other lines between these tables, delete them and keep only these two.

You now have a small "star": applications and questions around the answers. Whatever you pick from `Applications`
or `Questions` filters the answers.

## Tutorial 3: Chart the answers to one question

**Goal:** a bar chart of how many applications gave each answer to one question, for example "Type of visit".

1. Go back to **Report view** and add a **Clustered bar chart**.
2. Drag `Answers[answer_text]` to the **Y-axis**.
3. Drag `Answers[application_id]` to the **X-axis**, and set it to **Count (Distinct)**.
4. In the **Filters** pane, drag `Questions[question]` to **Filters on this visual**, and tick the question you
   want.

For a question with a number answer, put `Answers[number_answer]` in a **Card** visual and choose **Average**,
**Sum** or **Max**. For a date question, use `Answers[date_answer]` on the X-axis of a column chart.

Add a **Slicer** with `Questions[question]` to let report viewers pick the question themselves.

## Tutorial 4: One row per application, one column per question (matrix)

**Goal:** the classic grid: each application down the side, each question across the top, the answer in each cell.

### Create a measure

A measure tells Power BI what to show in each cell.

1. In the **Data** pane, right-click `Answers` and select **New measure**.
2. Paste this and press Enter:

   ```dax
   Answer = CONCATENATEX(VALUES(Answers[answer_text]), Answers[answer_text], ", ")
   ```

### Build the matrix

1. Add a **Matrix** visual.
2. Drag `Applications[application_reference]` to **Rows**.
3. Drag `Questions[question]` to **Columns**.
4. Drag the `Answer` measure to **Values**.

To show the questions in the order they appear in the form, select `question` in the `Questions` table, then
**Column tools** > **Sort by column** > `question_order`.

To show only some questions, add `Questions[question]` to **Filters on this visual** and tick the ones you want.
To add more application details, drag `lifecycle`, `created_on` or `last_submitted_at` into **Rows** under the
reference, then turn off **Stepped layout** in **Format** > **Row headers**.

New questions appear as new columns automatically after the next refresh.

## Tutorial 5: A flat wide table you can filter and export

**Goal:** a real table with one column per question, for example to export to Excel or to build visuals that need
each question as its own field.

The matrix in tutorial 4 is usually enough. Use this when you need proper columns.

1. Select **Transform data** to open Power Query.
2. Right-click the `Answers` query and select **Reference**. Rename the new query to `Applications wide`.
3. Select the `application_id`, `question_key` and `answer_text` columns (hold Ctrl), right-click one of them and
   select **Remove Other Columns**.
4. Select the `question_key` column, then **Transform** > **Pivot Column**.
5. Set **Values Column** to `answer_text`. Expand **Advanced options** and set **Aggregate Value Function** to
   **Don't Aggregate**. Select **OK**.
6. Select **Close & Apply**.
7. In **Model view**, link `Applications[application_id]` to `Applications wide[application_id]` (1 to 1).

Each question key is now a column. You can rename columns in Power Query, and change a column's type (for example
to **Date** or **Decimal Number**) with **Transform** > **Data Type**.

**Good to know:**
- If the form gains a question, the next refresh adds a column. Visuals that use the existing columns keep working.
- If you've renamed or retyped a column in Power Query and that question is later retired from the form, the step
  that refers to it fails. Delete the step and refresh.

## Tutorial 6: Checkbox questions

**Goal:** count how many applications ticked each option of a checkbox question.

In `Answers`, a checkbox question already shows all ticked options in one cell ("Finance, Governance"). That's fine
for the matrix but you can't count individual options from it. Load the ticks as their own table:

1. **Get data** > **Azure SQL database**, with this SQL statement, and name the query `Ticks`:

   ```sql
   SELECT
       f.application_id,
       COALESCE(f.semantic_key, f.field_id) AS question_key,
       COALESCE(o.label, f.value_string) AS ticked_option
   FROM prism.v_current_answer_facts f
   INNER JOIN prism.field_catalog c
       ON c.tenant_id = f.tenant_id
      AND c.template_version_id = f.template_version_id
      AND c.parent_field_id = f.parent_field_id
      AND c.field_id = f.field_id
      AND c.contract_version = f.contract_version
   OUTER APPLY (
       SELECT TOP 1 o.label
       FROM OPENJSON(c.choices_json) WITH (value nvarchar(400) '$.value', label nvarchar(400) '$.label') o
       WHERE o.value = f.value_string) o
   WHERE f.tenant_id = '<tenant-id>'
     AND f.template_id = '<template-id>'
     AND f.parent_field_id = ''
     AND LOWER(c.control_type) = 'checkboxes'
     AND f.nested_path <> ''
   ```

2. In **Model view**, link `Applications[application_id]` to `Ticks[application_id]` and
   `Questions[question_key]` to `Ticks[question_key]`.
3. Build a bar chart with `Ticks[ticked_option]` on the axis and `Ticks[application_id]` as **Count (Distinct)**.
   Filter on `Questions[question]` as in tutorial 3.

## Tutorial 7: Lookup questions (schools, trusts, MPs)

**Goal:** report on the details of something picked from a search, such as a school's URN or an MP's constituency.

When a user picks from a search box, FlexForms saves the whole record. In `Answers`, the cell shows its name.
Prism also keeps each detail of the record as a separate row, with the detail's name in `nested_path` (for example
`urn`, `constituencyName` or `displayName`).

1. Load this as a query named `Lookup details`:

   ```sql
   SELECT
       f.application_id,
       COALESCE(f.semantic_key, f.field_id) AS question_key,
       f.nested_path AS detail,
       COALESCE(f.value_string, CONVERT(nvarchar(50), f.value_decimal),
                CASE f.value_bool WHEN 1 THEN 'Yes' WHEN 0 THEN 'No' END) AS value
   FROM prism.v_current_answer_facts f
   INNER JOIN prism.field_catalog c
       ON c.tenant_id = f.tenant_id
      AND c.template_version_id = f.template_version_id
      AND c.parent_field_id = f.parent_field_id
      AND c.field_id = f.field_id
      AND c.contract_version = f.contract_version
   WHERE f.tenant_id = '<tenant-id>'
     AND f.template_id = '<template-id>'
     AND f.parent_field_id = ''
     AND f.nested_path <> ''
     AND LOWER(c.control_type) <> 'checkboxes'
   ```

2. Link it to `Applications` on `application_id`.
3. To see which details exist, add a table visual with `Lookup details[question_key]` and `detail`.
4. To show, say, the URN in the matrix from tutorial 4, add a measure:

   ```dax
   URN = CALCULATE(MAX('Lookup details'[value]), 'Lookup details'[detail] = "urn")
   ```

   and drag it into the matrix **Values**.

If a question allows several picks, each pick's details start with `#0/`, `#1/` and so on, for example `#0/urn`.
File upload questions also appear in this table, with one row per file; filter them out with
`Lookup details[question_key]` if you don't need them.

## Tutorial 8: Repeating sections (attendees, organisations)

**Goal:** report on sections the user can add several times, such as a list of attendees.

Each repeat (each attendee) is an **item**, with its own ID. Load all repeating-section answers as one table:

1. Load this as a query named `Section items`:

   ```sql
   SELECT
       f.application_id,
       f.parent_field_id AS section,
       f.item_id,
       f.item_ordinal + 1 AS item_number,
       COALESCE(f.semantic_key, f.parent_field_id + '/' + f.field_id) AS question_key,
       f.label AS question,
       COALESCE(
           choice.label,
           f.value_string,
           CONVERT(nvarchar(50), f.value_decimal),
           CASE f.value_bool WHEN 1 THEN 'Yes' WHEN 0 THEN 'No' END,
           CONVERT(nvarchar(10), f.value_date, 103),
           f.raw_value) AS answer_text
   FROM prism.v_current_answer_facts f
   LEFT JOIN prism.field_catalog c
       ON c.tenant_id = f.tenant_id
      AND c.template_version_id = f.template_version_id
      AND c.parent_field_id = f.parent_field_id
      AND c.field_id = f.field_id
      AND c.contract_version = f.contract_version
   OUTER APPLY (
       SELECT TOP 1 o.label
       FROM OPENJSON(c.choices_json) WITH (value nvarchar(400) '$.value', label nvarchar(400) '$.label') o
       WHERE o.value = f.value_string) choice
   WHERE f.tenant_id = '<tenant-id>'
     AND f.template_id = '<template-id>'
     AND f.parent_field_id <> ''
     AND f.nested_path = ''
   ```

2. Link `Applications[application_id]` to `Section items[application_id]`. Don't link it to `Questions`.
3. Add a **Slicer** on `Section items[section]` and pick one section, for example `externalAttendees`.
4. Add a **Matrix**:
   - **Rows:** `Applications[application_reference]`, then `Section items[item_number]`.
   - **Columns:** `Section items[question]`.
   - **Values:** a measure
     `Item answer = CONCATENATEX(VALUES('Section items'[answer_text]), 'Section items'[answer_text], ", ")`.

You get one line per attendee under each application. To count attendees per application, use
`Section items[item_id]` with **Count (Distinct)**.

`item_number` is only for display: it changes if the user reorders or removes items. Use `item_id` if you need to
tell items apart.

To build a wide table per section (one row per attendee), follow tutorial 5 on a reference of `Section items`
filtered to one section, keeping `application_id`, `item_id`, `question` and `answer_text`, and pivoting on
`question`.

## Tutorial 9: Submitted applications only, and what was submitted

**Only submitted applications.** Add `Applications[lifecycle]` to **Filters on all pages** and tick `Submitted`.
Drafts are left out everywhere, because every other table is filtered through `Applications`.

**Submission dates.** `Applications[last_submitted_at]` is the latest submission. Use it on a chart axis exactly
like `created_on` in tutorial 1.

**The answers as they were when submitted.** The tables so far show the latest answers, which can change if an
application is reopened. For an audit-style report of exactly what was submitted, load `Answers` again with the
SQL from tutorial 2, but:
- replace `prism.v_current_answer_facts` with `prism.v_submission_answer_facts`;
- replace `f.lifecycle` with `f.submission_id, f.submitted_at`.

Name it `Submitted answers` and link it to `Applications` and `Questions` the same way. An application submitted
twice has two sets of answers, one per `submission_id`.

## Tutorial 10: Keep an eye on template changes

**Goal:** a page that shows what changed in each new version of the form, so you know when to check your reports.

1. Load this as `Form changes`:

   ```sql
   SELECT version_number, version_created_on, change_type, semantic_key AS question_key,
          previous_label, label, field_id_changed AS renamed, label_changed AS reworded,
          type_changed, choices_changed, replaced_by, export_decision
   FROM prism.v_template_field_changes
   WHERE tenant_id = '<tenant-id>'
     AND template_id = '<template-id>'
   ```

2. Add a **Table** visual with all the columns, sorted by `version_created_on`, newest first.

How to read it:

| You see | What it means for your report |
|---|---|
| `Changed`, renamed | The author renamed the question. Your report keeps working. |
| `Changed`, reworded | Same question, new wording. Check the meaning hasn't changed. |
| `Added` | A new question. It appears as a new column after it's approved for export. |
| `Removed` with `replaced_by` | The question was retired and replaced, for example `name` by `firstName, lastName`. Older applications keep the old answers; decide how to show both. |
| `export_decision` is `Unclassified` | The question is waiting for approval and won't appear in your report until then. |

## Publish and refresh

1. Select **Home** > **Publish** and choose a workspace.
2. In the Power BI service, open the workspace, find the semantic model (dataset), and open **Settings**.
3. Under **Data source credentials**, select **Edit credentials**, choose **OAuth2**, and sign in.
4. Under **Refresh**, turn on scheduled refresh and pick a time. Once a day is plenty for most reports; Prism data
   is normally up to date within seconds of a change in FlexForms.

If the refresh fails with a network error, the database is probably on a private network. Ask the admin whether
you need to use a data gateway.

## Troubleshooting

| Problem | What to do |
|---|---|
| "Cannot open server" or a login error | Check you've been added to `prism_reader`, and that you chose **Microsoft account** when signing in. |
| "The SELECT permission was denied" | You're reading something other than the five views. Copy the SQL from this guide again. |
| A question is missing | It hasn't been approved for export yet. Ask the Prism admin. |
| A whole application is missing | Deleted applications are removed from Prism. Otherwise check your tenant and template IDs. |
| Answers show codes like `opt_1` | The option's wording wasn't in the form. Ask the form author to add it. |
| `Sort by column` fails with "more than one value" | Two questions have the same wording. Sort by `question_key` instead, or ask the author to make the wording unique. |
| `Answer` shows several values in one cell | The application gave several answers, for example a multi-select. That's expected. |
| `created_on` or `application_reference` is empty | Prism hasn't refreshed that application since an upgrade. It fills in overnight. |
| Pivot step fails after a refresh | A question used by a renamed or retyped column was retired. Delete that step in Power Query and refresh. |
