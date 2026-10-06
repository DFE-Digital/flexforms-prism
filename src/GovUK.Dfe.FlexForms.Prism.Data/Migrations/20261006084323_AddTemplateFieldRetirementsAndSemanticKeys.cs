using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GovUK.Dfe.FlexForms.Prism.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateFieldRetirementsAndSemanticKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "template_field_retirements",
                schema: "prism",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    parent_field_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    field_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    replaced_by = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_field_retirements", x => new { x.tenant_id, x.template_version_id, x.parent_field_id, x.field_id });
                });

            migrationBuilder.Sql(PrismViews.DropTemplateFieldChanges);
            migrationBuilder.Sql(PrismViews.CreateTemplateFieldChangesBySemanticKey);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PrismViews.DropTemplateFieldChanges);
            migrationBuilder.Sql(PrismViews.CreateTemplateFieldChangesWithDefaults);

            migrationBuilder.DropTable(
                name: "template_field_retirements",
                schema: "prism");
        }
    }
}
