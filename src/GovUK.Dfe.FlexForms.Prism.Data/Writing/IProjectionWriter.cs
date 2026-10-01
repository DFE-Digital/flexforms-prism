using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;

namespace GovUK.Dfe.FlexForms.Prism.Data.Writing;

public interface IProjectionWriter
{
    /// <summary>
    /// Writes a new Current generation and swaps the application's active pointer to it, if the projection is
    /// newer than the stored state.
    /// </summary>
    Task<WriteResult> WriteCurrentAsync(CurrentProjection projection, IReadOnlyCollection<AnswerFact> facts, CancellationToken cancellationToken);

    /// <summary>
    /// Advances the state metadata (revision, response, lifecycle) without a new generation, for when the
    /// flattened facts hash to the same value as the active generation.
    /// </summary>
    Task<WriteResult> AdvanceCurrentAsync(CurrentProjection projection, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a Submission generation and selects it on the submission snapshot. A stored snapshot is only
    /// replaced by a higher projector or contract version.
    /// </summary>
    Task<WriteResult> WriteSubmissionAsync(SubmissionProjection projection, IReadOnlyCollection<AnswerFact> facts, CancellationToken cancellationToken);

    /// <summary>
    /// Records a tombstone and marks the application state as Deleted.
    /// </summary>
    Task<WriteResult> RecordDeletionAsync(DeletionRecord deletion, CancellationToken cancellationToken);
}
