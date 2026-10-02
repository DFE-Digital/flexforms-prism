using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GovUK.Dfe.FlexForms.Prism.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExportDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "export_defaults",
                schema: "prism",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    policy_version = table.Column<int>(type: "int", nullable: false),
                    reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    decided_by = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    decided_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_defaults", x => new { x.tenant_id, x.template_id });
                });

            migrationBuilder.Sql(PrismViews.DropTemplateFieldChanges);
            migrationBuilder.Sql(PrismViews.CreateTemplateFieldChangesWithDefaults);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PrismViews.DropTemplateFieldChanges);
            migrationBuilder.Sql(PrismViews.CreateTemplateFieldChanges);

            migrationBuilder.DropTable(
                name: "export_defaults",
                schema: "prism");
        }
    }
}
