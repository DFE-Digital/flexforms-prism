using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GovUK.Dfe.FlexForms.Prism.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialPrismSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "prism");

            migrationBuilder.CreateTable(
                name: "backfill_operations",
                schema: "prism",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    modified_since = table.Column<DateTime>(type: "datetime2", nullable: true),
                    requested_by = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    pages_processed = table.Column<int>(type: "int", nullable: false),
                    messages_enqueued = table.Column<int>(type: "int", nullable: false),
                    error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backfill_operations", x => x.operation_id);
                });

            migrationBuilder.CreateTable(
                name: "deletion_tombstones",
                schema: "prism",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    application_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_revision = table.Column<long>(type: "bigint", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deletion_tombstones", x => new { x.tenant_id, x.application_id });
                });

            migrationBuilder.CreateTable(
                name: "field_catalog",
                schema: "prism",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    parent_field_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    field_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    contract_version = table.Column<int>(type: "int", nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_version_number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    flow_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    flow_mode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    task_group_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    task_group_name = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    task_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    task_name = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    page_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    page_title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    field_order = table.Column<int>(type: "int", nullable: true),
                    label = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    data_type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    control_type = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    is_collection = table.Column<bool>(type: "bit", nullable: false),
                    is_required = table.Column<bool>(type: "bit", nullable: true),
                    choices_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    sensitivity = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    semantic_key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    export_status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_catalog", x => new { x.tenant_id, x.template_version_id, x.parent_field_id, x.field_id, x.contract_version });
                });

            migrationBuilder.CreateTable(
                name: "field_export_policy",
                schema: "prism",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    parent_field_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    field_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    decision = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    policy_version = table.Column<int>(type: "int", nullable: false),
                    reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    decided_by = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    decided_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_export_policy", x => new { x.tenant_id, x.template_id, x.parent_field_id, x.field_id });
                });

            migrationBuilder.CreateTable(
                name: "projection_generations",
                schema: "prism",
                columns: table => new
                {
                    generation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    application_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    source_revision = table.Column<long>(type: "bigint", nullable: false),
                    response_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    response_revision = table.Column<long>(type: "bigint", nullable: true),
                    submission_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    projector_version = table.Column<int>(type: "int", nullable: false),
                    contract_version = table.Column<int>(type: "int", nullable: false),
                    export_policy_version = table.Column<int>(type: "int", nullable: false),
                    fact_count = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    activated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    superseded_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projection_generations", x => x.generation_id);
                });

            migrationBuilder.CreateTable(
                name: "schema_info",
                schema: "prism",
                columns: table => new
                {
                    key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schema_info", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "answer_facts",
                schema: "prism",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    generation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    application_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    logical_key_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    field_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    parent_field_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    occurrence_path = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    item_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    item_ordinal = table.Column<int>(type: "int", nullable: true),
                    nested_path = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    data_type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    is_completed = table.Column<bool>(type: "bit", nullable: true),
                    interpretation_status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    value_string = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    value_decimal = table.Column<decimal>(type: "decimal(38,10)", precision: 38, scale: 10, nullable: true),
                    value_bool = table.Column<bool>(type: "bit", nullable: true),
                    value_date = table.Column<DateOnly>(type: "date", nullable: true),
                    value_date_time = table.Column<DateTime>(type: "datetime2", nullable: true),
                    value_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    raw_value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_answer_facts", x => x.id);
                    table.ForeignKey(
                        name: "FK_answer_facts_projection_generations_generation_id",
                        column: x => x.generation_id,
                        principalSchema: "prism",
                        principalTable: "projection_generations",
                        principalColumn: "generation_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "application_projection_state",
                schema: "prism",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    application_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    active_generation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    response_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    source_revision = table.Column<long>(type: "bigint", nullable: false),
                    lifecycle = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    template_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    source_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: true),
                    projector_version = table.Column<int>(type: "int", nullable: false),
                    contract_version = table.Column<int>(type: "int", nullable: false),
                    export_policy_version = table.Column<int>(type: "int", nullable: false),
                    source_occurred_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    projected_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_projection_state", x => new { x.tenant_id, x.application_id });
                    table.ForeignKey(
                        name: "FK_application_projection_state_projection_generations_active_generation_id",
                        column: x => x.active_generation_id,
                        principalSchema: "prism",
                        principalTable: "projection_generations",
                        principalColumn: "generation_id");
                });

            migrationBuilder.CreateTable(
                name: "submission_snapshots",
                schema: "prism",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    submission_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    application_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    response_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    response_revision = table.Column<long>(type: "bigint", nullable: true),
                    source_revision = table.Column<long>(type: "bigint", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    selected_generation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    projector_version = table.Column<int>(type: "int", nullable: false),
                    contract_version = table.Column<int>(type: "int", nullable: false),
                    export_policy_version = table.Column<int>(type: "int", nullable: false),
                    projected_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_submission_snapshots", x => new { x.tenant_id, x.submission_id });
                    table.ForeignKey(
                        name: "FK_submission_snapshots_projection_generations_selected_generation_id",
                        column: x => x.selected_generation_id,
                        principalSchema: "prism",
                        principalTable: "projection_generations",
                        principalColumn: "generation_id");
                });

            migrationBuilder.InsertData(
                schema: "prism",
                table: "schema_info",
                columns: new[] { "key", "value" },
                values: new object[] { "contract_version", "1" });

            migrationBuilder.CreateIndex(
                name: "IX_answer_facts_generation_id_logical_key_hash",
                schema: "prism",
                table: "answer_facts",
                columns: new[] { "generation_id", "logical_key_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_answer_facts_tenant_id_field_id",
                schema: "prism",
                table: "answer_facts",
                columns: new[] { "tenant_id", "field_id" });

            migrationBuilder.CreateIndex(
                name: "IX_application_projection_state_active_generation_id",
                schema: "prism",
                table: "application_projection_state",
                column: "active_generation_id");

            migrationBuilder.CreateIndex(
                name: "IX_application_projection_state_tenant_id_projected_at",
                schema: "prism",
                table: "application_projection_state",
                columns: new[] { "tenant_id", "projected_at" });

            migrationBuilder.CreateIndex(
                name: "IX_backfill_operations_status_created_at",
                schema: "prism",
                table: "backfill_operations",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_field_catalog_tenant_id_template_id_field_id",
                schema: "prism",
                table: "field_catalog",
                columns: new[] { "tenant_id", "template_id", "field_id" });

            migrationBuilder.CreateIndex(
                name: "IX_projection_generations_status_superseded_at",
                schema: "prism",
                table: "projection_generations",
                columns: new[] { "status", "superseded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_projection_generations_tenant_id_application_id_kind_status",
                schema: "prism",
                table: "projection_generations",
                columns: new[] { "tenant_id", "application_id", "kind", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_submission_snapshots_selected_generation_id",
                schema: "prism",
                table: "submission_snapshots",
                column: "selected_generation_id");

            migrationBuilder.CreateIndex(
                name: "IX_submission_snapshots_tenant_id_application_id",
                schema: "prism",
                table: "submission_snapshots",
                columns: new[] { "tenant_id", "application_id" });

            migrationBuilder.CreateIndex(
                name: "IX_submission_snapshots_tenant_id_submitted_at",
                schema: "prism",
                table: "submission_snapshots",
                columns: new[] { "tenant_id", "submitted_at" });

            migrationBuilder.Sql(PrismViews.CreateCurrentAnswerFacts);
            migrationBuilder.Sql(PrismViews.CreateSubmissionAnswerFacts);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PrismViews.DropSubmissionAnswerFacts);
            migrationBuilder.Sql(PrismViews.DropCurrentAnswerFacts);

            migrationBuilder.DropTable(
                name: "answer_facts",
                schema: "prism");

            migrationBuilder.DropTable(
                name: "application_projection_state",
                schema: "prism");

            migrationBuilder.DropTable(
                name: "backfill_operations",
                schema: "prism");

            migrationBuilder.DropTable(
                name: "deletion_tombstones",
                schema: "prism");

            migrationBuilder.DropTable(
                name: "field_catalog",
                schema: "prism");

            migrationBuilder.DropTable(
                name: "field_export_policy",
                schema: "prism");

            migrationBuilder.DropTable(
                name: "schema_info",
                schema: "prism");

            migrationBuilder.DropTable(
                name: "submission_snapshots",
                schema: "prism");

            migrationBuilder.DropTable(
                name: "projection_generations",
                schema: "prism");
        }
    }
}
