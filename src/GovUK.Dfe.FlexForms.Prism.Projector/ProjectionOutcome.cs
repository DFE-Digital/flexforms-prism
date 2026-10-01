namespace GovUK.Dfe.FlexForms.Prism.Projector;

public enum ProjectionStatus
{
    /// <summary>Something was written.</summary>
    Projected,

    /// <summary>Nothing needed writing, see <see cref="ProjectionOutcome.Reason"/>.</summary>
    Skipped
}

public sealed record ProjectionOutcome(ProjectionStatus Status, string Reason, Guid? GenerationId = null)
{
    public static ProjectionOutcome Skipped(string reason) => new(ProjectionStatus.Skipped, reason);

    public static ProjectionOutcome Projected(string reason, Guid? generationId) => new(ProjectionStatus.Projected, reason, generationId);
}

public static class ProjectionReasons
{
    public const string NewGeneration = "new_generation";
    public const string HashReused = "hash_reused";
    public const string Deleted = "deleted";

    public const string Tombstoned = "tombstoned";
    public const string AlreadyProjected = "already_projected";
    public const string NotNewer = "not_newer";
    public const string CasConflict = "cas_conflict";
    public const string NoResponse = "no_response";
}
