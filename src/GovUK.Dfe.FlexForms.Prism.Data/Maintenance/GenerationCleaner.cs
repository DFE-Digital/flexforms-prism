using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace GovUK.Dfe.FlexForms.Prism.Data.Maintenance;

public sealed record CleanupResult(int GenerationsDeleted, long FactsDeleted);

public interface IGenerationCleaner
{
    /// <summary>
    /// Deletes up to <paramref name="maxGenerations"/> generations that were superseded before
    /// <paramref name="supersededBefore"/> and are not referenced by any state or snapshot, with their facts.
    /// </summary>
    Task<CleanupResult> DeleteSupersededAsync(DateTime supersededBefore, int maxGenerations, CancellationToken cancellationToken);
}

/// <summary>
/// Deletes facts in small batches outside a long transaction. The reference checks are repeated in the final
/// delete, and the foreign keys reject deleting a referenced generation regardless.
/// </summary>
public sealed class GenerationCleaner(PrismDbContext db) : IGenerationCleaner
{
    private const int FactBatchSize = 10_000;
    private const int ForeignKeyViolation = 547;

    private const string Unreferenced = """

        AND NOT EXISTS (SELECT 1 FROM prism.application_projection_state s WHERE s.active_generation_id = g.generation_id)
        AND NOT EXISTS (SELECT 1 FROM prism.submission_snapshots s WHERE s.selected_generation_id = g.generation_id)

        """;

    private const string SelectCandidates = """
        SELECT TOP (@max) g.generation_id AS Value
        FROM prism.projection_generations g
        WHERE g.status = @status AND g.superseded_at < @cutoff
        """ + Unreferenced + "ORDER BY g.superseded_at";

    private const string DeleteGeneration = """
        DELETE g FROM prism.projection_generations g
        WHERE g.generation_id = @generation AND g.status = @status
        """ + Unreferenced;

    public async Task<CleanupResult> DeleteSupersededAsync(DateTime supersededBefore, int maxGenerations, CancellationToken cancellationToken)
    {
        var superseded = nameof(GenerationStatus.Superseded);
        var candidates = await db.Database
            .SqlQueryRaw<Guid>(
                SelectCandidates,
                new SqlParameter("@max", maxGenerations),
                new SqlParameter("@status", superseded),
                new SqlParameter("@cutoff", supersededBefore))
            .ToListAsync(cancellationToken);

        var generations = 0;
        long facts = 0;
        foreach (var generationId in candidates)
        {
            int deleted;
            do
            {
                deleted = await db.Database.ExecuteSqlRawAsync(
                    "DELETE TOP (@batch) FROM prism.answer_facts WHERE generation_id = @generation",
                    [new SqlParameter("@batch", FactBatchSize), new SqlParameter("@generation", generationId)],
                    cancellationToken);
                facts += deleted;
            }
            while (deleted == FactBatchSize);

            try
            {
                generations += await db.Database.ExecuteSqlRawAsync(
                    DeleteGeneration,
                    [new SqlParameter("@generation", generationId), new SqlParameter("@status", superseded)],
                    cancellationToken);
            }
            catch (SqlException ex) when (ex.Number == ForeignKeyViolation)
            {
                // Referenced after all; it stays.
            }
        }

        return new CleanupResult(generations, facts);
    }
}
