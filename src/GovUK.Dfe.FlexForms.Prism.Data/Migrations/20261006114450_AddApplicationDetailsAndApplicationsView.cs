using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GovUK.Dfe.FlexForms.Prism.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationDetailsAndApplicationsView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "application_created_on",
                schema: "prism",
                table: "application_projection_state",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "application_last_modified_on",
                schema: "prism",
                table: "application_projection_state",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "application_reference",
                schema: "prism",
                table: "application_projection_state",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql(PrismViews.CreateApplications);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PrismViews.DropApplications);

            migrationBuilder.DropColumn(
                name: "application_created_on",
                schema: "prism",
                table: "application_projection_state");

            migrationBuilder.DropColumn(
                name: "application_last_modified_on",
                schema: "prism",
                table: "application_projection_state");

            migrationBuilder.DropColumn(
                name: "application_reference",
                schema: "prism",
                table: "application_projection_state");
        }
    }
}
