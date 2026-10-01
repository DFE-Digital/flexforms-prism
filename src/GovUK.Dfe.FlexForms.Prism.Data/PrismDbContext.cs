using System.Text;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using Microsoft.EntityFrameworkCore;

namespace GovUK.Dfe.FlexForms.Prism.Data;

public class PrismDbContext(DbContextOptions<PrismDbContext> options) : DbContext(options)
{
    public const string Schema = "prism";

    public DbSet<ApplicationProjectionState> ApplicationProjectionStates => Set<ApplicationProjectionState>();
    public DbSet<ProjectionGeneration> ProjectionGenerations => Set<ProjectionGeneration>();
    public DbSet<SubmissionSnapshot> SubmissionSnapshots => Set<SubmissionSnapshot>();
    public DbSet<AnswerFactEntity> AnswerFacts => Set<AnswerFactEntity>();
    public DbSet<FieldCatalogEntry> FieldCatalog => Set<FieldCatalogEntry>();
    public DbSet<FieldExportPolicy> FieldExportPolicies => Set<FieldExportPolicy>();
    public DbSet<DeletionTombstone> DeletionTombstones => Set<DeletionTombstone>();
    public DbSet<BackfillOperation> BackfillOperations => Set<BackfillOperation>();
    public DbSet<SchemaInfo> SchemaInfoEntries => Set<SchemaInfo>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<GenerationKind>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<GenerationStatus>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<ApplicationLifecycle>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<ExportStatus>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<ExportDecision>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<BackfillStatus>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<OperationKind>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<InterpretationStatus>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime2");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ProjectionGeneration>(e =>
        {
            e.ToTable("projection_generations");
            e.HasKey(x => x.GenerationId);
            e.Property(x => x.SourceHash).HasMaxLength(32).IsFixedLength();
            e.HasIndex(x => new { x.TenantId, x.ApplicationId, x.Kind, x.Status });
            e.HasIndex(x => new { x.Status, x.SupersededAt });
        });

        modelBuilder.Entity<ApplicationProjectionState>(e =>
        {
            e.ToTable("application_projection_state");
            e.HasKey(x => new { x.TenantId, x.ApplicationId });
            e.Property(x => x.SourceHash).HasMaxLength(32).IsFixedLength();
            e.HasOne(x => x.ActiveGeneration)
                .WithMany()
                .HasForeignKey(x => x.ActiveGenerationId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.ActiveGenerationId);
            e.HasIndex(x => new { x.TenantId, x.ProjectedAt });
        });

        modelBuilder.Entity<SubmissionSnapshot>(e =>
        {
            e.ToTable("submission_snapshots");
            e.HasKey(x => new { x.TenantId, x.SubmissionId });
            e.Property(x => x.SourceHash).HasMaxLength(32).IsFixedLength();
            e.HasOne(x => x.SelectedGeneration)
                .WithMany()
                .HasForeignKey(x => x.SelectedGenerationId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.SelectedGenerationId);
            e.HasIndex(x => new { x.TenantId, x.ApplicationId });
            e.HasIndex(x => new { x.TenantId, x.SubmittedAt });
        });

        modelBuilder.Entity<AnswerFactEntity>(e =>
        {
            e.ToTable("answer_facts");
            e.HasKey(x => x.Id);
            e.Property(x => x.LogicalKeyHash).HasMaxLength(32).IsFixedLength();
            e.Property(x => x.FieldId).HasMaxLength(200);
            e.Property(x => x.ParentFieldId).HasMaxLength(200);
            e.Property(x => x.OccurrencePath).HasMaxLength(1000);
            e.Property(x => x.ItemId).HasMaxLength(200);
            e.Property(x => x.NestedPath).HasMaxLength(300);
            e.Property(x => x.DataType).HasMaxLength(50);
            e.Property(x => x.ValueDecimal).HasPrecision(38, 10);
            e.HasOne(x => x.Generation)
                .WithMany(x => x.Facts)
                .HasForeignKey(x => x.GenerationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.GenerationId, x.LogicalKeyHash }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.FieldId });
        });

        modelBuilder.Entity<FieldCatalogEntry>(e =>
        {
            e.ToTable("field_catalog");
            e.HasKey(x => new { x.TenantId, x.TemplateVersionId, x.ParentFieldId, x.FieldId, x.ContractVersion });
            e.Property(x => x.FieldId).HasMaxLength(200);
            e.Property(x => x.TemplateVersionNumber).HasMaxLength(50);
            e.Property(x => x.ParentFieldId).HasMaxLength(200);
            e.Property(x => x.FlowId).HasMaxLength(200);
            e.Property(x => x.FlowMode).HasMaxLength(40);
            e.Property(x => x.TaskGroupId).HasMaxLength(200);
            e.Property(x => x.TaskGroupName).HasMaxLength(500);
            e.Property(x => x.TaskId).HasMaxLength(200);
            e.Property(x => x.TaskName).HasMaxLength(500);
            e.Property(x => x.PageId).HasMaxLength(200);
            e.Property(x => x.PageTitle).HasMaxLength(500);
            e.Property(x => x.Label).HasMaxLength(1000);
            e.Property(x => x.DataType).HasMaxLength(50);
            e.Property(x => x.ControlType).HasMaxLength(80);
            e.Property(x => x.Sensitivity).HasMaxLength(40);
            e.Property(x => x.SemanticKey).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.TemplateId, x.FieldId });
        });

        modelBuilder.Entity<FieldExportPolicy>(e =>
        {
            e.ToTable("field_export_policy");
            e.HasKey(x => new { x.TenantId, x.TemplateId, x.ParentFieldId, x.FieldId });
            e.Property(x => x.ParentFieldId).HasMaxLength(200);
            e.Property(x => x.FieldId).HasMaxLength(200);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.DecidedBy).HasMaxLength(256);
        });

        modelBuilder.Entity<DeletionTombstone>(e =>
        {
            e.ToTable("deletion_tombstones");
            e.HasKey(x => new { x.TenantId, x.ApplicationId });
        });

        modelBuilder.Entity<BackfillOperation>(e =>
        {
            e.ToTable("backfill_operations");
            e.HasKey(x => x.OperationId);
            e.Property(x => x.RequestedBy).HasMaxLength(256);
            e.Property(x => x.CancelledBy).HasMaxLength(256);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });

        modelBuilder.Entity<SchemaInfo>(e =>
        {
            e.ToTable("schema_info");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Value).HasMaxLength(400);
            e.HasData(new SchemaInfo
            {
                Key = SchemaInfo.ContractVersionKey,
                Value = SchemaInfo.CurrentContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        });

        ApplySnakeCaseColumnNames(modelBuilder);
    }

    private static void ApplySnakeCaseColumnNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
