using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GovUK.Dfe.FlexForms.Prism.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadApplicantContact : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "lead_applicant_email",
                schema: "prism",
                table: "application_projection_state",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lead_applicant_name",
                schema: "prism",
                table: "application_projection_state",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql(PrismViews.DropApplications);
            migrationBuilder.Sql(PrismViews.CreateApplicationsWithLeadApplicantContact);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PrismViews.DropApplications);
            migrationBuilder.Sql(PrismViews.CreateApplicationsWithLeadApplicant);

            migrationBuilder.DropColumn(
                name: "lead_applicant_email",
                schema: "prism",
                table: "application_projection_state");

            migrationBuilder.DropColumn(
                name: "lead_applicant_name",
                schema: "prism",
                table: "application_projection_state");
        }
    }
}
