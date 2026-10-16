using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using Microsoft.EntityFrameworkCore;
using static GovUK.Dfe.FlexForms.Prism.Data.Tests.TestData;

namespace GovUK.Dfe.FlexForms.Prism.Data.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class ApplicationsViewTests(SqlServerFixture sql)
{
    private static readonly DateTime CreatedOn = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ModifiedOn = new(2026, 9, 2, 10, 30, 0, DateTimeKind.Utc);
    private static readonly Guid LeadApplicant = Guid.NewGuid();

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _application = Guid.NewGuid();

    [Fact]
    public async Task Each_projected_application_is_one_row_with_its_reference_lifecycle_and_dates()
    {
        await WriteCurrent(Current(_tenant, _application, 3, lifecycle: ApplicationLifecycle.Submitted) with
        {
            Details = new ApplicationDetails("APP-123", CreatedOn, ModifiedOn, LeadApplicant, "Lead Applicant", "lead@example.com"),
        });

        var row = Assert.Single(await Rows());
        Assert.Equal("APP-123", row.application_reference);
        Assert.Equal(LeadApplicant, row.lead_applicant_user_id);
        Assert.Equal("Lead Applicant", row.lead_applicant_name);
        Assert.Equal("lead@example.com", row.lead_applicant_email);
        Assert.Equal("Submitted", row.lifecycle);
        Assert.Equal(CreatedOn, row.created_on);
        Assert.Equal(ModifiedOn, row.last_modified_on);
        Assert.Equal(3, row.source_revision);
        Assert.Null(row.last_submitted_at);
    }

    [Fact]
    public async Task An_application_never_modified_shows_its_created_date_as_last_modified()
    {
        await WriteCurrent(Current(_tenant, _application, 1) with { Details = new ApplicationDetails("APP-1", CreatedOn, null) });

        Assert.Equal(CreatedOn, Assert.Single(await Rows()).last_modified_on);
    }

    [Fact]
    public async Task A_later_revision_replaces_the_details()
    {
        await WriteCurrent(Current(_tenant, _application, 1) with { Details = new ApplicationDetails("APP-1", CreatedOn, null) });
        await WriteCurrent(Current(_tenant, _application, 2, hashSeed: "other") with { Details = new ApplicationDetails("APP-1", CreatedOn, ModifiedOn) });

        var row = Assert.Single(await Rows());
        Assert.Equal(ModifiedOn, row.last_modified_on);
        Assert.Equal(2, row.source_revision);
    }

    [Fact]
    public async Task The_latest_submission_date_is_shown()
    {
        await WriteCurrent(Current(_tenant, _application, 2) with { Details = new ApplicationDetails("APP-1", CreatedOn, ModifiedOn) });
        await using (var db = sql.CreateContext())
        {
            await new ProjectionWriter(db, TimeProvider.System).WriteSubmissionAsync(
                Submission(_tenant, _application, Guid.NewGuid(), 2), [Fact("f", "submitted")], CancellationToken.None);
        }

        Assert.NotNull(Assert.Single(await Rows()).last_submitted_at);
    }

    [Fact]
    public async Task A_deleted_application_is_hidden()
    {
        await WriteCurrent(Current(_tenant, _application, 1) with { Details = new ApplicationDetails("APP-1", CreatedOn, null) });
        await using (var db = sql.CreateContext())
        {
            await new ProjectionWriter(db, TimeProvider.System).RecordDeletionAsync(
                new DeletionRecord(_tenant, _application, 2, DateTime.UtcNow), CancellationToken.None);
        }

        Assert.Empty(await Rows());
    }

    [Fact]
    public async Task Rows_projected_before_details_were_recorded_have_null_details()
    {
        await WriteCurrent(Current(_tenant, _application, 1));

        var row = Assert.Single(await Rows());
        Assert.Null(row.application_reference);
        Assert.Null(row.lead_applicant_user_id);
        Assert.Null(row.lead_applicant_name);
        Assert.Null(row.lead_applicant_email);
        Assert.Null(row.created_on);
        Assert.Null(row.last_modified_on);
    }

    [Fact]
    public async Task Template_and_tenant_names_come_from_the_catalogued_version_and_tenants_directory()
    {
        var projection = Current(_tenant, _application, 1) with
        {
            Details = new ApplicationDetails("APP-1", CreatedOn, null),
        };
        await WriteCurrent(projection);

        await using (var db = sql.CreateContext())
        {
            db.TemplateVersions.Add(new TemplateVersionEntry
            {
                TenantId = _tenant,
                TemplateVersionId = projection.TemplateVersionId,
                TemplateId = projection.TemplateId,
                VersionNumber = "1.4.0",
                TemplateName = "Transfer application",
                CreatedOn = CreatedOn,
                CataloguedAt = CreatedOn,
            });
            db.Tenants.Add(new TenantEntry
            {
                TenantId = _tenant,
                TenantName = "Academies",
                UpdatedAt = CreatedOn,
            });
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await Rows());
        Assert.Equal("1.4.0", row.template_version_number);
        Assert.Equal("Transfer application", row.template_name);
        Assert.Equal("Academies", row.tenant_name);
    }

    [Fact]
    public async Task Template_and_tenant_columns_are_null_until_catalogued()
    {
        await WriteCurrent(Current(_tenant, _application, 1) with { Details = new ApplicationDetails("APP-1", CreatedOn, null) });

        var row = Assert.Single(await Rows());
        Assert.Null(row.template_version_number);
        Assert.Null(row.template_name);
        Assert.Null(row.tenant_name);
    }

    private async Task WriteCurrent(CurrentProjection projection)
    {
        await using var db = sql.CreateContext();
        var result = await new ProjectionWriter(db, TimeProvider.System).WriteCurrentAsync(projection, [Fact("f", "v")], CancellationToken.None);
        Assert.Equal(WriteOutcome.Applied, result.Outcome);
    }

    private async Task<List<ApplicationRow>> Rows()
    {
        await using var db = sql.CreateContext();
        return await db.Database
            .SqlQuery<ApplicationRow>($"""
                SELECT application_reference, lead_applicant_user_id, lead_applicant_name, lead_applicant_email,
                       lifecycle, created_on, last_modified_on, last_submitted_at, source_revision,
                       template_version_number, template_name, tenant_name
                FROM prism.v_applications
                WHERE tenant_id = {_tenant} AND application_id = {_application}
                """)
            .ToListAsync();
    }

#pragma warning disable IDE1006
    private sealed record ApplicationRow(
        string? application_reference,
        Guid? lead_applicant_user_id,
        string? lead_applicant_name,
        string? lead_applicant_email,
        string lifecycle,
        DateTime? created_on,
        DateTime? last_modified_on,
        DateTime? last_submitted_at,
        long source_revision,
        string? template_version_number,
        string? template_name,
        string? tenant_name);
#pragma warning restore IDE1006
}
