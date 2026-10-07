using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GovUK.Dfe.FlexForms.Prism.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateVersionsAndFieldChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "template_versions",
                schema: "prism",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version_number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    created_on = table.Column<DateTime>(type: "datetime2", nullable: false),
                    catalogued_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_versions", x => new { x.tenant_id, x.template_version_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_template_versions_tenant_id_template_id_created_on",
                schema: "prism",
                table: "template_versions",
                columns: new[] { "tenant_id", "template_id", "created_on" });

            // The source creation time is unknown for versions catalogued before this migration; the catalogue time
            // stands in until the version is next catalogued, which corrects it.
            migrationBuilder.Sql("""
                INSERT INTO prism.template_versions (tenant_id, template_version_id, template_id, version_number, created_on, catalogued_at)
                SELECT tenant_id, template_version_id, MIN(template_id), MAX(template_version_number), MIN(created_at), MIN(created_at)
                FROM prism.field_catalog
                GROUP BY tenant_id, template_version_id;
                """);

            migrationBuilder.Sql(PrismViews.CreateTemplateFieldChanges);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PrismViews.DropTemplateFieldChanges);

            migrationBuilder.DropTable(
                name: "template_versions",
                schema: "prism");
        }
    }
}
