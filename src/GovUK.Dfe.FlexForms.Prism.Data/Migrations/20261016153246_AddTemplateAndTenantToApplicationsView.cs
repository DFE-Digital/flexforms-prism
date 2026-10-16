using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GovUK.Dfe.FlexForms.Prism.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateAndTenantToApplicationsView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "template_name",
                schema: "prism",
                table: "template_versions",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "prism",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.tenant_id);
                });

            migrationBuilder.Sql(PrismViews.DropApplications);
            migrationBuilder.Sql(PrismViews.CreateApplicationsWithTemplateAndTenant);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PrismViews.DropApplications);
            migrationBuilder.Sql(PrismViews.CreateApplicationsWithLeadApplicantContact);

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "prism");

            migrationBuilder.DropColumn(
                name: "template_name",
                schema: "prism",
                table: "template_versions");
        }
    }
}
