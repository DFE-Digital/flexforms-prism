namespace GovUK.Dfe.FlexForms.Prism.Data.Migrations;

/// <summary>
/// Consumer-facing views. Facts are only reachable through the active (state) or selected (snapshot)
/// generation pointer, so Building and Superseded generations are never visible, and tombstoned applications
/// are hidden. A later migration that changes a view must recreate it with its full definition.
/// </summary>
internal static class PrismViews
{
    private const string FactColumns = """
            f.field_id,
            f.parent_field_id,
            f.occurrence_path,
            f.item_id,
            f.item_ordinal,
            f.nested_path,
            f.data_type,
            f.is_completed,
            f.interpretation_status,
            f.value_string,
            f.value_decimal,
            f.value_bool,
            f.value_date,
            f.value_date_time,
            f.value_json,
            f.raw_value,
            c.label,
            c.task_group_id,
            c.task_group_name,
            c.task_id,
            c.task_name,
            c.page_id,
            c.page_title,
            c.semantic_key
        """;

    private const string CatalogJoin = """
        LEFT JOIN prism.field_catalog c
            ON c.tenant_id = g.tenant_id
           AND c.template_version_id = g.template_version_id
           AND c.parent_field_id = f.parent_field_id
           AND c.field_id = f.field_id
           AND c.contract_version = g.contract_version
        """;

    public const string CreateCurrentAnswerFacts = $"""
        CREATE VIEW prism.v_current_answer_facts AS
        SELECT
            s.tenant_id,
            s.application_id,
            s.lifecycle,
            s.source_revision,
            s.response_id,
            s.template_id,
            s.template_version_id,
            s.projected_at,
            g.generation_id,
            g.projector_version,
            g.contract_version,
            g.export_policy_version,
            {FactColumns}
        FROM prism.application_projection_state s
        INNER JOIN prism.projection_generations g
            ON g.generation_id = s.active_generation_id
           AND g.status = 'Active'
        INNER JOIN prism.answer_facts f
            ON f.generation_id = g.generation_id
        {CatalogJoin}
        WHERE s.lifecycle <> 'Deleted'
          AND NOT EXISTS (
              SELECT 1 FROM prism.deletion_tombstones t
              WHERE t.tenant_id = s.tenant_id AND t.application_id = s.application_id)
        """;

    public const string CreateSubmissionAnswerFacts = $"""
        CREATE VIEW prism.v_submission_answer_facts AS
        SELECT
            ss.tenant_id,
            ss.application_id,
            ss.submission_id,
            ss.response_id,
            ss.response_revision,
            ss.source_revision,
            ss.submitted_at,
            ss.template_id,
            ss.template_version_id,
            ss.projected_at,
            g.generation_id,
            g.projector_version,
            g.contract_version,
            g.export_policy_version,
            {FactColumns}
        FROM prism.submission_snapshots ss
        INNER JOIN prism.projection_generations g
            ON g.generation_id = ss.selected_generation_id
           AND g.status = 'Active'
        INNER JOIN prism.answer_facts f
            ON f.generation_id = g.generation_id
        {CatalogJoin}
        WHERE NOT EXISTS (
            SELECT 1 FROM prism.deletion_tombstones t
            WHERE t.tenant_id = ss.tenant_id AND t.application_id = ss.application_id)
        """;

    /// <summary>
    /// One row per field that was added, removed or changed by each template version, compared with the version
    /// created before it. Every field of a template's first catalogued version is Added. Fields are matched by
    /// (parent_field_id, field_id); labels and choices are compared case-sensitively.
    /// </summary>
    public const string CreateTemplateFieldChanges = $"""
        CREATE VIEW prism.v_template_field_changes AS
        {TemplateFieldChangesDiffs}
        SELECT
            d.*,
            COALESCE(fep.decision, 'Unclassified') AS export_decision
        FROM diffs d
        {TemplateFieldChangesDecisionJoin}
        {TemplateFieldChangesFilter}
        """;

    /// <summary>
    /// <see cref="CreateTemplateFieldChanges"/> with export_decision following the export default for undecided
    /// fields: AllowedByDefault when the template's (or else the tenant's) default is ExportAll.
    /// </summary>
    public const string CreateTemplateFieldChangesWithDefaults = $"""
        CREATE VIEW prism.v_template_field_changes AS
        {TemplateFieldChangesDiffs}
        SELECT
            d.*,
            COALESCE(
                fep.decision,
                CASE WHEN COALESCE(tpl.mode, ten.mode) = 'ExportAll' THEN 'AllowedByDefault' ELSE 'Unclassified' END) AS export_decision
        FROM diffs d
        {TemplateFieldChangesDecisionJoin}
        LEFT JOIN prism.export_defaults tpl
            ON tpl.tenant_id = d.tenant_id
           AND tpl.template_id = d.template_id
        LEFT JOIN prism.export_defaults ten
            ON ten.tenant_id = d.tenant_id
           AND ten.template_id = '00000000-0000-0000-0000-000000000000'
        {TemplateFieldChangesFilter}
        """;

    /// <summary>
    /// <see cref="CreateTemplateFieldChangesWithDefaults"/> matching fields across versions by semantic key rather
    /// than field id, so a field the author renamed while keeping its semantic key is one Changed row with
    /// field_id_changed set. Removed rows carry the author's replaced_by from the version's retiredFields.
    /// Rows catalogued before semantic keys were recorded fall back to the key the field id implies.
    /// </summary>
    public const string CreateTemplateFieldChangesBySemanticKey = $"""
        CREATE VIEW prism.v_template_field_changes AS
        {TemplateFieldChangesDiffsBySemanticKey}
        SELECT
            d.*,
            r.replaced_by,
            COALESCE(
                fep.decision,
                CASE WHEN COALESCE(tpl.mode, ten.mode) = 'ExportAll' THEN 'AllowedByDefault' ELSE 'Unclassified' END) AS export_decision
        FROM diffs d
        {TemplateFieldChangesDecisionJoin}
        LEFT JOIN prism.export_defaults tpl
            ON tpl.tenant_id = d.tenant_id
           AND tpl.template_id = d.template_id
        LEFT JOIN prism.export_defaults ten
            ON ten.tenant_id = d.tenant_id
           AND ten.template_id = '00000000-0000-0000-0000-000000000000'
        LEFT JOIN prism.template_field_retirements r
            ON d.change_type = 'Removed'
           AND r.tenant_id = d.tenant_id
           AND r.template_version_id = d.template_version_id
           AND r.parent_field_id = d.parent_field_id
           AND r.field_id = d.field_id
        WHERE d.change_type <> 'Changed'
           OR d.label_changed = 1 OR d.type_changed = 1 OR d.required_changed = 1 OR d.choices_changed = 1 OR d.location_changed = 1
           OR d.field_id_changed = 1
        """;

    private const string TemplateFieldChangesDiffsBySemanticKey = """
        WITH versions AS (
            SELECT
                tv.tenant_id,
                tv.template_id,
                tv.template_version_id,
                tv.version_number,
                tv.created_on,
                LAG(tv.template_version_id) OVER (PARTITION BY tv.tenant_id, tv.template_id ORDER BY tv.created_on, tv.template_version_id) AS previous_template_version_id,
                LAG(tv.version_number) OVER (PARTITION BY tv.tenant_id, tv.template_id ORDER BY tv.created_on, tv.template_version_id) AS previous_version_number
            FROM prism.template_versions tv
        ),
        catalog AS (
            SELECT
                fc.*,
                COALESCE(fc.semantic_key, CASE WHEN fc.parent_field_id = '' THEN fc.field_id ELSE fc.parent_field_id + '/' + fc.field_id END) AS match_key
            FROM prism.field_catalog fc
            WHERE fc.contract_version = (SELECT TRY_CAST(si.value AS int) FROM prism.schema_info si WHERE si.[key] = 'contract_version')
        ),
        diffs AS (
            SELECT
                v.tenant_id,
                v.template_id,
                v.template_version_id,
                v.version_number,
                v.created_on AS version_created_on,
                v.previous_template_version_id,
                v.previous_version_number,
                COALESCE(cur.parent_field_id, prev.parent_field_id) AS parent_field_id,
                COALESCE(cur.field_id, prev.field_id) AS field_id,
                prev.parent_field_id AS previous_parent_field_id,
                prev.field_id AS previous_field_id,
                COALESCE(cur.match_key, prev.match_key) AS semantic_key,
                CASE WHEN prev.field_id IS NULL THEN 'Added' WHEN cur.field_id IS NULL THEN 'Removed' ELSE 'Changed' END AS change_type,
                CAST(CASE WHEN cur.field_id IS NOT NULL AND prev.field_id IS NOT NULL AND EXISTS (
                    SELECT cur.parent_field_id COLLATE Latin1_General_100_BIN2, cur.field_id COLLATE Latin1_General_100_BIN2
                    EXCEPT SELECT prev.parent_field_id COLLATE Latin1_General_100_BIN2, prev.field_id COLLATE Latin1_General_100_BIN2) THEN 1 ELSE 0 END AS bit) AS field_id_changed,
                CAST(CASE WHEN cur.field_id IS NOT NULL AND prev.field_id IS NOT NULL AND EXISTS (
                    SELECT cur.label COLLATE Latin1_General_100_BIN2
                    EXCEPT SELECT prev.label COLLATE Latin1_General_100_BIN2) THEN 1 ELSE 0 END AS bit) AS label_changed,
                CAST(CASE WHEN cur.field_id IS NOT NULL AND prev.field_id IS NOT NULL AND EXISTS (
                    SELECT cur.data_type, cur.control_type, cur.is_collection
                    EXCEPT SELECT prev.data_type, prev.control_type, prev.is_collection) THEN 1 ELSE 0 END AS bit) AS type_changed,
                CAST(CASE WHEN cur.field_id IS NOT NULL AND prev.field_id IS NOT NULL AND EXISTS (
                    SELECT cur.is_required EXCEPT SELECT prev.is_required) THEN 1 ELSE 0 END AS bit) AS required_changed,
                CAST(CASE WHEN cur.field_id IS NOT NULL AND prev.field_id IS NOT NULL AND EXISTS (
                    SELECT cur.choices_json COLLATE Latin1_General_100_BIN2
                    EXCEPT SELECT prev.choices_json COLLATE Latin1_General_100_BIN2) THEN 1 ELSE 0 END AS bit) AS choices_changed,
                CAST(CASE WHEN cur.field_id IS NOT NULL AND prev.field_id IS NOT NULL AND EXISTS (
                    SELECT cur.task_id, cur.page_id, cur.flow_id
                    EXCEPT SELECT prev.task_id, prev.page_id, prev.flow_id) THEN 1 ELSE 0 END AS bit) AS location_changed,
                prev.label AS previous_label,
                cur.label,
                prev.data_type AS previous_data_type,
                cur.data_type,
                prev.control_type AS previous_control_type,
                cur.control_type,
                prev.is_required AS previous_is_required,
                cur.is_required,
                prev.choices_json AS previous_choices_json,
                cur.choices_json,
                prev.task_name AS previous_task_name,
                cur.task_name,
                prev.page_title AS previous_page_title,
                cur.page_title,
                COALESCE(cur.contract_version, prev.contract_version) AS contract_version
            FROM versions v
            CROSS APPLY (
                SELECT
                    c.parent_field_id, c.field_id, c.match_key, c.label, c.data_type, c.control_type, c.is_collection, c.is_required,
                    c.choices_json, c.task_id, c.task_name, c.page_id, c.page_title, c.flow_id, c.contract_version,
                    p.parent_field_id AS p_parent_field_id, p.field_id AS p_field_id, p.match_key AS p_match_key, p.label AS p_label,
                    p.data_type AS p_data_type, p.control_type AS p_control_type, p.is_collection AS p_is_collection,
                    p.is_required AS p_is_required, p.choices_json AS p_choices_json, p.task_id AS p_task_id, p.task_name AS p_task_name,
                    p.page_id AS p_page_id, p.page_title AS p_page_title, p.flow_id AS p_flow_id, p.contract_version AS p_contract_version
                FROM (SELECT * FROM catalog WHERE tenant_id = v.tenant_id AND template_version_id = v.template_version_id) c
                FULL OUTER JOIN (SELECT * FROM catalog WHERE tenant_id = v.tenant_id AND template_version_id = v.previous_template_version_id) p
                    ON p.match_key = c.match_key
            ) j
            CROSS APPLY (SELECT
                j.parent_field_id, j.field_id, j.match_key, j.label, j.data_type, j.control_type, j.is_collection, j.is_required,
                j.choices_json, j.task_id, j.task_name, j.page_id, j.page_title, j.flow_id, j.contract_version) cur
            CROSS APPLY (SELECT
                j.p_parent_field_id AS parent_field_id, j.p_field_id AS field_id, j.p_match_key AS match_key, j.p_label AS label,
                j.p_data_type AS data_type, j.p_control_type AS control_type, j.p_is_collection AS is_collection,
                j.p_is_required AS is_required, j.p_choices_json AS choices_json, j.p_task_id AS task_id, j.p_task_name AS task_name,
                j.p_page_id AS page_id, j.p_page_title AS page_title, j.p_flow_id AS flow_id, j.p_contract_version AS contract_version) prev
        )
        """;

    private const string TemplateFieldChangesDecisionJoin = """
        LEFT JOIN prism.field_export_policy fep
            ON fep.tenant_id = d.tenant_id
           AND fep.template_id = d.template_id
           AND fep.parent_field_id = d.parent_field_id
           AND fep.field_id = d.field_id
        """;

    private const string TemplateFieldChangesFilter = """
        WHERE d.change_type <> 'Changed'
           OR d.label_changed = 1 OR d.type_changed = 1 OR d.required_changed = 1 OR d.choices_changed = 1 OR d.location_changed = 1
        """;

    private const string TemplateFieldChangesDiffs = """
        WITH versions AS (
            SELECT
                tv.tenant_id,
                tv.template_id,
                tv.template_version_id,
                tv.version_number,
                tv.created_on,
                LAG(tv.template_version_id) OVER (PARTITION BY tv.tenant_id, tv.template_id ORDER BY tv.created_on, tv.template_version_id) AS previous_template_version_id,
                LAG(tv.version_number) OVER (PARTITION BY tv.tenant_id, tv.template_id ORDER BY tv.created_on, tv.template_version_id) AS previous_version_number
            FROM prism.template_versions tv
        ),
        catalog AS (
            SELECT fc.*
            FROM prism.field_catalog fc
            WHERE fc.contract_version = (SELECT TRY_CAST(si.value AS int) FROM prism.schema_info si WHERE si.[key] = 'contract_version')
        ),
        diffs AS (
            SELECT
                v.tenant_id,
                v.template_id,
                v.template_version_id,
                v.version_number,
                v.created_on AS version_created_on,
                v.previous_template_version_id,
                v.previous_version_number,
                COALESCE(cur.parent_field_id, prev.parent_field_id) AS parent_field_id,
                COALESCE(cur.field_id, prev.field_id) AS field_id,
                CASE WHEN prev.field_id IS NULL THEN 'Added' WHEN cur.field_id IS NULL THEN 'Removed' ELSE 'Changed' END AS change_type,
                CAST(CASE WHEN cur.field_id IS NOT NULL AND prev.field_id IS NOT NULL AND EXISTS (
                    SELECT cur.label COLLATE Latin1_General_100_BIN2
                    EXCEPT SELECT prev.label COLLATE Latin1_General_100_BIN2) THEN 1 ELSE 0 END AS bit) AS label_changed,
                CAST(CASE WHEN cur.field_id IS NOT NULL AND prev.field_id IS NOT NULL AND EXISTS (
                    SELECT cur.data_type, cur.control_type, cur.is_collection
                    EXCEPT SELECT prev.data_type, prev.control_type, prev.is_collection) THEN 1 ELSE 0 END AS bit) AS type_changed,
                CAST(CASE WHEN cur.field_id IS NOT NULL AND prev.field_id IS NOT NULL AND EXISTS (
                    SELECT cur.is_required EXCEPT SELECT prev.is_required) THEN 1 ELSE 0 END AS bit) AS required_changed,
                CAST(CASE WHEN cur.field_id IS NOT NULL AND prev.field_id IS NOT NULL AND EXISTS (
                    SELECT cur.choices_json COLLATE Latin1_General_100_BIN2
                    EXCEPT SELECT prev.choices_json COLLATE Latin1_General_100_BIN2) THEN 1 ELSE 0 END AS bit) AS choices_changed,
                CAST(CASE WHEN cur.field_id IS NOT NULL AND prev.field_id IS NOT NULL AND EXISTS (
                    SELECT cur.task_id, cur.page_id, cur.flow_id
                    EXCEPT SELECT prev.task_id, prev.page_id, prev.flow_id) THEN 1 ELSE 0 END AS bit) AS location_changed,
                prev.label AS previous_label,
                cur.label,
                prev.data_type AS previous_data_type,
                cur.data_type,
                prev.control_type AS previous_control_type,
                cur.control_type,
                prev.is_required AS previous_is_required,
                cur.is_required,
                prev.choices_json AS previous_choices_json,
                cur.choices_json,
                prev.task_name AS previous_task_name,
                cur.task_name,
                prev.page_title AS previous_page_title,
                cur.page_title,
                COALESCE(cur.contract_version, prev.contract_version) AS contract_version
            FROM versions v
            CROSS APPLY (
                SELECT
                    c.parent_field_id, c.field_id, c.label, c.data_type, c.control_type, c.is_collection, c.is_required,
                    c.choices_json, c.task_id, c.task_name, c.page_id, c.page_title, c.flow_id, c.contract_version,
                    p.parent_field_id AS p_parent_field_id, p.field_id AS p_field_id, p.label AS p_label, p.data_type AS p_data_type,
                    p.control_type AS p_control_type, p.is_collection AS p_is_collection, p.is_required AS p_is_required,
                    p.choices_json AS p_choices_json, p.task_id AS p_task_id, p.task_name AS p_task_name, p.page_id AS p_page_id,
                    p.page_title AS p_page_title, p.flow_id AS p_flow_id, p.contract_version AS p_contract_version
                FROM (SELECT * FROM catalog WHERE tenant_id = v.tenant_id AND template_version_id = v.template_version_id) c
                FULL OUTER JOIN (SELECT * FROM catalog WHERE tenant_id = v.tenant_id AND template_version_id = v.previous_template_version_id) p
                    ON p.parent_field_id = c.parent_field_id AND p.field_id = c.field_id
            ) j
            CROSS APPLY (SELECT
                j.parent_field_id, j.field_id, j.label, j.data_type, j.control_type, j.is_collection, j.is_required,
                j.choices_json, j.task_id, j.task_name, j.page_id, j.page_title, j.flow_id, j.contract_version) cur
            CROSS APPLY (SELECT
                j.p_parent_field_id AS parent_field_id, j.p_field_id AS field_id, j.p_label AS label, j.p_data_type AS data_type,
                j.p_control_type AS control_type, j.p_is_collection AS is_collection, j.p_is_required AS is_required,
                j.p_choices_json AS choices_json, j.p_task_id AS task_id, j.p_task_name AS task_name, j.p_page_id AS page_id,
                j.p_page_title AS page_title, j.p_flow_id AS flow_id, j.p_contract_version AS contract_version) prev
        )
        """;

    public const string DropTemplateFieldChanges = "DROP VIEW IF EXISTS prism.v_template_field_changes;";

    public const string DropCurrentAnswerFacts = "DROP VIEW IF EXISTS prism.v_current_answer_facts;";

    public const string DropSubmissionAnswerFacts = "DROP VIEW IF EXISTS prism.v_submission_answer_facts;";
}
