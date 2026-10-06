namespace GovUK.Dfe.FlexForms.Prism.Flattener;

/// <summary>
/// Versions stamped on every generation. Bump <see cref="ProjectorVersion"/> when flattening output changes
/// for the same input, and <see cref="ContractVersion"/> when the consumer-facing shape of facts or views changes.
/// Either bump makes the projector re-project revisions that were already projected.
/// </summary>
public static class PrismVersions
{
    // 2: application details on the state row, and semantic keys and retirements in the catalogue.
    public const int ProjectorVersion = 2;
    public const int ContractVersion = 1;
}
