using System.Data;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace GovUK.Dfe.FlexForms.Prism.Data.Writing;

/// <summary>
/// Writes projections in a single transaction. The state (or snapshot) row is locked with UPDLOCK, HOLDLOCK
/// before anything is written, which also serialises the first insert for an application, so the
/// version-aware compare-and-swap is decided before facts are copied.
/// </summary>
public sealed class ProjectionWriter(PrismDbContext db, TimeProvider clock) : IProjectionWriter
{
    private const int BulkCopyBatchSize = 5000;

    public Task<WriteResult> WriteCurrentAsync(CurrentProjection projection, IReadOnlyCollection<AnswerFact> facts, CancellationToken cancellationToken)
        => InTransactionAsync(async (connection, transaction) =>
        {
            var state = await LockStateAsync(connection, transaction, projection.TenantId, projection.ApplicationId, cancellationToken);
            if (await IsTombstonedAsync(connection, transaction, projection.TenantId, projection.ApplicationId, cancellationToken))
            {
                return WriteResult.Tombstoned;
            }

            if (state is not null && !IsNewer(state.Value, projection.SourceRevision, projection.Versions))
            {
                return WriteResult.Stale;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var generationId = Guid.CreateVersion7();

            await InsertGenerationAsync(connection, transaction, new GenerationRow(
                generationId, projection.TenantId, projection.ApplicationId, GenerationKind.Current, projection.SourceRevision,
                projection.ResponseId, projection.ResponseRevision, null, projection.TemplateId, projection.TemplateVersionId,
                projection.SourceHash, projection.Versions, facts.Count, now), cancellationToken);

            await BulkCopyFactsAsync(connection, transaction, generationId, projection.TenantId, projection.ApplicationId, facts, cancellationToken);

            if (state is null)
            {
                await InsertStateAsync(connection, transaction, projection, generationId, now, cancellationToken);
            }
            else if (!await UpdateStateAsync(connection, transaction, projection, generationId, now, cancellationToken))
            {
                return WriteResult.Stale;
            }

            var previous = state?.ActiveGenerationId;
            await ActivateAsync(connection, transaction, generationId, previous, now, cancellationToken);
            return new WriteResult(WriteOutcome.Applied, generationId, previous);
        }, cancellationToken);

    public Task<WriteResult> AdvanceCurrentAsync(CurrentProjection projection, CancellationToken cancellationToken)
        => InTransactionAsync(async (connection, transaction) =>
        {
            var state = await LockStateAsync(connection, transaction, projection.TenantId, projection.ApplicationId, cancellationToken);
            if (await IsTombstonedAsync(connection, transaction, projection.TenantId, projection.ApplicationId, cancellationToken))
            {
                return WriteResult.Tombstoned;
            }

            if (state?.ActiveGenerationId is not { } activeGenerationId)
            {
                throw new InvalidOperationException(
                    $"Application {projection.ApplicationId} has no active generation to advance; write a new generation instead.");
            }

            if (!IsNewer(state.Value, projection.SourceRevision, projection.Versions))
            {
                return WriteResult.Stale;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            return await UpdateStateAsync(connection, transaction, projection, activeGenerationId, now, cancellationToken)
                ? new WriteResult(WriteOutcome.Applied, activeGenerationId)
                : WriteResult.Stale;
        }, cancellationToken);

    public Task<WriteResult> WriteSubmissionAsync(SubmissionProjection projection, IReadOnlyCollection<AnswerFact> facts, CancellationToken cancellationToken)
        => InTransactionAsync(async (connection, transaction) =>
        {
            var snapshot = await LockSnapshotAsync(connection, transaction, projection.TenantId, projection.SubmissionId, cancellationToken);
            if (await IsTombstonedAsync(connection, transaction, projection.TenantId, projection.ApplicationId, cancellationToken))
            {
                return WriteResult.Tombstoned;
            }

            if (snapshot is not null && !IsNewerVersion(snapshot.Value.ProjectorVersion, snapshot.Value.ContractVersion, projection.Versions))
            {
                return WriteResult.Stale;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var generationId = Guid.CreateVersion7();

            await InsertGenerationAsync(connection, transaction, new GenerationRow(
                generationId, projection.TenantId, projection.ApplicationId, GenerationKind.Submission, projection.SourceRevision,
                projection.ResponseId, projection.ResponseRevision, projection.SubmissionId, projection.TemplateId,
                projection.TemplateVersionId, projection.SourceHash, projection.Versions, facts.Count, now), cancellationToken);

            await BulkCopyFactsAsync(connection, transaction, generationId, projection.TenantId, projection.ApplicationId, facts, cancellationToken);

            await UpsertSnapshotAsync(connection, transaction, projection, generationId, exists: snapshot is not null, now, cancellationToken);

            var previous = snapshot?.SelectedGenerationId;
            await ActivateAsync(connection, transaction, generationId, previous, now, cancellationToken);
            return new WriteResult(WriteOutcome.Applied, generationId, previous);
        }, cancellationToken);

    public Task<WriteResult> RecordDeletionAsync(DeletionRecord deletion, CancellationToken cancellationToken)
        => InTransactionAsync(async (connection, transaction) =>
        {
            var state = await LockStateAsync(connection, transaction, deletion.TenantId, deletion.ApplicationId, cancellationToken);

            await using (var read = Command(connection, transaction, """
                SELECT source_revision FROM prism.deletion_tombstones WITH (UPDLOCK, HOLDLOCK)
                WHERE tenant_id = @tenant AND application_id = @application
                """,
                ("@tenant", deletion.TenantId), ("@application", deletion.ApplicationId)))
            {
                if (await read.ExecuteScalarAsync(cancellationToken) is long storedRevision && storedRevision >= deletion.SourceRevision)
                {
                    return WriteResult.Stale;
                }
            }

            var now = clock.GetUtcNow().UtcDateTime;

            await ExecuteAsync(connection, transaction, """
                MERGE prism.deletion_tombstones AS target
                USING (SELECT @tenant AS tenant_id, @application AS application_id) AS source
                    ON target.tenant_id = source.tenant_id AND target.application_id = source.application_id
                WHEN MATCHED THEN
                    UPDATE SET source_revision = @revision, deleted_at = @deletedAt, recorded_at = @now
                WHEN NOT MATCHED THEN
                    INSERT (tenant_id, application_id, source_revision, deleted_at, recorded_at)
                    VALUES (@tenant, @application, @revision, @deletedAt, @now);
                """, cancellationToken,
                ("@tenant", deletion.TenantId), ("@application", deletion.ApplicationId), ("@revision", deletion.SourceRevision),
                ("@deletedAt", deletion.DeletedAt), ("@now", now));

            if (state is null)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO prism.application_projection_state
                        (tenant_id, application_id, active_generation_id, response_id, source_revision, lifecycle,
                         template_id, template_version_id, source_hash, projector_version, contract_version,
                         source_occurred_at, created_at, projected_at)
                    VALUES (@tenant, @application, NULL, NULL, @revision, @lifecycle, NULL, NULL, NULL, 0, 0, @deletedAt, @now, @now)
                    """, cancellationToken,
                    ("@tenant", deletion.TenantId), ("@application", deletion.ApplicationId), ("@revision", deletion.SourceRevision),
                    ("@lifecycle", nameof(ApplicationLifecycle.Deleted)), ("@deletedAt", deletion.DeletedAt), ("@now", now));
            }
            else
            {
                await ExecuteAsync(connection, transaction, """
                    UPDATE prism.application_projection_state
                    SET lifecycle = @lifecycle,
                        source_revision = CASE WHEN source_revision < @revision THEN @revision ELSE source_revision END,
                        source_occurred_at = @deletedAt,
                        projected_at = @now
                    WHERE tenant_id = @tenant AND application_id = @application
                    """, cancellationToken,
                    ("@tenant", deletion.TenantId), ("@application", deletion.ApplicationId), ("@revision", deletion.SourceRevision),
                    ("@lifecycle", nameof(ApplicationLifecycle.Deleted)), ("@deletedAt", deletion.DeletedAt), ("@now", now));
            }

            return new WriteResult(WriteOutcome.Applied, state?.ActiveGenerationId);
        }, cancellationToken);

    private async Task<WriteResult> InTransactionAsync(Func<SqlConnection, SqlTransaction, Task<WriteResult>> work, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            var result = await work(connection, transaction);
            if (result.Outcome == WriteOutcome.Applied)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return result;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static bool IsNewer(StateRow state, long sourceRevision, ProjectionVersions versions)
        => state.SourceRevision < sourceRevision
           || (state.SourceRevision == sourceRevision && IsNewerVersion(state.ProjectorVersion, state.ContractVersion, versions));

    private static bool IsNewerVersion(int storedProjectorVersion, int storedContractVersion, ProjectionVersions versions)
        => storedProjectorVersion < versions.ProjectorVersion
           || (storedProjectorVersion == versions.ProjectorVersion && storedContractVersion < versions.ContractVersion);

    private static async Task<StateRow?> LockStateAsync(SqlConnection connection, SqlTransaction transaction, Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT active_generation_id, source_revision, projector_version, contract_version
            FROM prism.application_projection_state WITH (UPDLOCK, HOLDLOCK)
            WHERE tenant_id = @tenant AND application_id = @application
            """,
            ("@tenant", tenantId), ("@application", applicationId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StateRow(
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    private static async Task<SnapshotRow?> LockSnapshotAsync(SqlConnection connection, SqlTransaction transaction, Guid tenantId, Guid submissionId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT selected_generation_id, projector_version, contract_version
            FROM prism.submission_snapshots WITH (UPDLOCK, HOLDLOCK)
            WHERE tenant_id = @tenant AND submission_id = @submission
            """,
            ("@tenant", tenantId), ("@submission", submissionId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SnapshotRow(reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private static async Task<bool> IsTombstonedAsync(SqlConnection connection, SqlTransaction transaction, Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM prism.deletion_tombstones
                WHERE tenant_id = @tenant AND application_id = @application) THEN 1 ELSE 0 END
            """,
            ("@tenant", tenantId), ("@application", applicationId));

        return (int)(await command.ExecuteScalarAsync(cancellationToken))! == 1;
    }

    private static Task InsertGenerationAsync(SqlConnection connection, SqlTransaction transaction, GenerationRow row, CancellationToken cancellationToken)
        => ExecuteAsync(connection, transaction, """
            INSERT INTO prism.projection_generations
                (generation_id, tenant_id, application_id, kind, status, source_revision, response_id, response_revision,
                 submission_id, template_id, template_version_id, source_hash, projector_version, contract_version,
                 export_policy_version, fact_count, created_at, activated_at, superseded_at)
            VALUES
                (@generation, @tenant, @application, @kind, @status, @revision, @response, @responseRevision,
                 @submission, @template, @templateVersion, @hash, @projectorVersion, @contractVersion,
                 @exportPolicyVersion, @factCount, @now, NULL, NULL)
            """, cancellationToken,
            ("@generation", row.GenerationId), ("@tenant", row.TenantId), ("@application", row.ApplicationId),
            ("@kind", row.Kind.ToString()), ("@status", nameof(GenerationStatus.Building)), ("@revision", row.SourceRevision),
            ("@response", row.ResponseId), ("@responseRevision", row.ResponseRevision), ("@submission", row.SubmissionId),
            ("@template", row.TemplateId), ("@templateVersion", row.TemplateVersionId), ("@hash", row.SourceHash),
            ("@projectorVersion", row.Versions.ProjectorVersion), ("@contractVersion", row.Versions.ContractVersion),
            ("@exportPolicyVersion", row.Versions.ExportPolicyVersion), ("@factCount", row.FactCount), ("@now", row.CreatedAt));

    private static Task InsertStateAsync(SqlConnection connection, SqlTransaction transaction, CurrentProjection projection, Guid generationId, DateTime now, CancellationToken cancellationToken)
        => ExecuteAsync(connection, transaction, """
            INSERT INTO prism.application_projection_state
                (tenant_id, application_id, active_generation_id, response_id, source_revision, lifecycle,
                 template_id, template_version_id, source_hash, projector_version, contract_version,
                 source_occurred_at, created_at, projected_at)
            VALUES
                (@tenant, @application, @generation, @response, @revision, @lifecycle,
                 @template, @templateVersion, @hash, @projectorVersion, @contractVersion,
                 @occurredAt, @now, @now)
            """, cancellationToken, StateParameters(projection, generationId, now));

    private static async Task<bool> UpdateStateAsync(SqlConnection connection, SqlTransaction transaction, CurrentProjection projection, Guid generationId, DateTime now, CancellationToken cancellationToken)
    {
        var updated = await ExecuteAsync(connection, transaction, """
            UPDATE prism.application_projection_state
            SET active_generation_id = @generation,
                response_id = @response,
                source_revision = @revision,
                lifecycle = @lifecycle,
                template_id = @template,
                template_version_id = @templateVersion,
                source_hash = @hash,
                projector_version = @projectorVersion,
                contract_version = @contractVersion,
                source_occurred_at = @occurredAt,
                projected_at = @now
            WHERE tenant_id = @tenant AND application_id = @application
              AND (source_revision < @revision
                   OR (source_revision = @revision
                       AND (projector_version < @projectorVersion
                            OR (projector_version = @projectorVersion AND contract_version < @contractVersion))))
            """, cancellationToken, StateParameters(projection, generationId, now));

        return updated == 1;
    }

    private static (string, object?)[] StateParameters(CurrentProjection projection, Guid generationId, DateTime now) =>
    [
        ("@tenant", projection.TenantId), ("@application", projection.ApplicationId), ("@generation", generationId),
        ("@response", projection.ResponseId), ("@revision", projection.SourceRevision), ("@lifecycle", projection.Lifecycle.ToString()),
        ("@template", projection.TemplateId), ("@templateVersion", projection.TemplateVersionId), ("@hash", projection.SourceHash),
        ("@projectorVersion", projection.Versions.ProjectorVersion), ("@contractVersion", projection.Versions.ContractVersion),
        ("@occurredAt", projection.SourceOccurredAt), ("@now", now)
    ];

    private static Task UpsertSnapshotAsync(SqlConnection connection, SqlTransaction transaction, SubmissionProjection projection, Guid generationId, bool exists, DateTime now, CancellationToken cancellationToken)
    {
        var sql = exists
            ? """
              UPDATE prism.submission_snapshots
              SET selected_generation_id = @generation, response_id = @response, response_revision = @responseRevision,
                  source_revision = @revision, submitted_at = @submittedAt, template_id = @template,
                  template_version_id = @templateVersion, source_hash = @hash, projector_version = @projectorVersion,
                  contract_version = @contractVersion, projected_at = @now
              WHERE tenant_id = @tenant AND submission_id = @submission
              """
            : """
              INSERT INTO prism.submission_snapshots
                  (tenant_id, submission_id, application_id, response_id, response_revision, source_revision, submitted_at,
                   template_id, template_version_id, selected_generation_id, source_hash, projector_version,
                   contract_version, projected_at)
              VALUES
                  (@tenant, @submission, @application, @response, @responseRevision, @revision, @submittedAt,
                   @template, @templateVersion, @generation, @hash, @projectorVersion,
                   @contractVersion, @now)
              """;

        return ExecuteAsync(connection, transaction, sql, cancellationToken,
            ("@tenant", projection.TenantId), ("@submission", projection.SubmissionId), ("@application", projection.ApplicationId),
            ("@response", projection.ResponseId), ("@responseRevision", projection.ResponseRevision), ("@revision", projection.SourceRevision),
            ("@submittedAt", projection.SubmittedAt), ("@template", projection.TemplateId), ("@templateVersion", projection.TemplateVersionId),
            ("@generation", generationId), ("@hash", projection.SourceHash), ("@projectorVersion", projection.Versions.ProjectorVersion),
            ("@contractVersion", projection.Versions.ContractVersion), ("@now", now));
    }

    private static async Task ActivateAsync(SqlConnection connection, SqlTransaction transaction, Guid generationId, Guid? previousGenerationId, DateTime now, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            UPDATE prism.projection_generations SET status = @status, activated_at = @now WHERE generation_id = @generation
            """, cancellationToken,
            ("@status", nameof(GenerationStatus.Active)), ("@now", now), ("@generation", generationId));

        if (previousGenerationId is { } previous)
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE prism.projection_generations SET status = @status, superseded_at = @now WHERE generation_id = @generation
                """, cancellationToken,
                ("@status", nameof(GenerationStatus.Superseded)), ("@now", now), ("@generation", previous));
        }
    }

    private static async Task BulkCopyFactsAsync(SqlConnection connection, SqlTransaction transaction, Guid generationId, Guid tenantId, Guid applicationId, IReadOnlyCollection<AnswerFact> facts, CancellationToken cancellationToken)
    {
        if (facts.Count == 0)
        {
            return;
        }

        using var table = AnswerFactTable.Create();
        foreach (var fact in facts)
        {
            AnswerFactTable.AddRow(table, generationId, tenantId, applicationId, fact);
        }

        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction)
        {
            DestinationTableName = "prism.answer_facts",
            BatchSize = BulkCopyBatchSize,
            BulkCopyTimeout = 0
        };

        foreach (DataColumn column in table.Columns)
        {
            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulkCopy.WriteToServerAsync(table, cancellationToken);
    }

    private static async Task<int> ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(value is byte[] bytes
                ? new SqlParameter(name, SqlDbType.Binary, bytes.Length) { Value = bytes }
                : new SqlParameter(name, value ?? DBNull.Value));
        }

        return command;
    }

    private readonly record struct StateRow(Guid? ActiveGenerationId, long SourceRevision, int ProjectorVersion, int ContractVersion);

    private readonly record struct SnapshotRow(Guid SelectedGenerationId, int ProjectorVersion, int ContractVersion);

    private sealed record GenerationRow(
        Guid GenerationId,
        Guid TenantId,
        Guid ApplicationId,
        GenerationKind Kind,
        long SourceRevision,
        Guid ResponseId,
        long? ResponseRevision,
        Guid? SubmissionId,
        Guid TemplateId,
        Guid TemplateVersionId,
        byte[] SourceHash,
        ProjectionVersions Versions,
        int FactCount,
        DateTime CreatedAt);
}
