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

    public const string DropCurrentAnswerFacts = "DROP VIEW IF EXISTS prism.v_current_answer_facts;";

    public const string DropSubmissionAnswerFacts = "DROP VIEW IF EXISTS prism.v_submission_answer_facts;";
}
