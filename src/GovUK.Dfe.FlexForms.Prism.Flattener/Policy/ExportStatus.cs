namespace GovUK.Dfe.FlexForms.Prism.Flattener.Policy;

/// <summary>
/// Export classification of a catalogued field. Only <see cref="Allowed"/> fields produce facts.
/// </summary>
public enum ExportStatus
{
    Unclassified,
    Allowed,
    Denied
}

/// <summary>
/// An explicit export decision recorded for a field.
/// </summary>
public enum ExportDecision
{
    Allowed,
    Denied
}
