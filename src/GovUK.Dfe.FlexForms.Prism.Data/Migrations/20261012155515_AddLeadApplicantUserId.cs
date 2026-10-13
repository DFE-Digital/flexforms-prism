using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GovUK.Dfe.FlexForms.Prism.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadApplicantUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "lead_applicant_user_id",
                schema: "prism",
                table: "application_projection_state",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql(PrismViews.DropApplications);
            migrationBuilder.Sql(PrismViews.CreateApplicationsWithLeadApplicant);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PrismViews.DropApplications);
            migrationBuilder.Sql(PrismViews.CreateApplications);

            migrationBuilder.DropColumn(
                name: "lead_applicant_user_id",
                schema: "prism",
                table: "application_projection_state");
        }
    }
}
