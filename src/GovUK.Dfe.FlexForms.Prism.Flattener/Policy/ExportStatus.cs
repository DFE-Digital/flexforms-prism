namespace GovUK.Dfe.FlexForms.Prism.Flattener.Policy;

/// <summary>
/// Export classification of a catalogued field. <see cref="Allowed"/> and <see cref="AllowedByDefault"/> fields
/// produce facts.
/// </summary>
public enum ExportStatus
{
    /// <summary>No decision, and the default withholds it.</summary>
    Unclassified,
    Allowed,
    Denied,

    /// <summary>No decision, but exported because the template's default is <see cref="DefaultExportMode.ExportAll"/>.</summary>
    AllowedByDefault
}

/// <summary>
/// An explicit export decision recorded for a field.
/// </summary>
public enum ExportDecision
{
    Allowed,
    Denied
}

/// <summary>
/// What happens to a field nobody has made a decision about.
/// </summary>
public enum DefaultExportMode
{
    /// <summary>Withheld until someone allows it.</summary>
    ApproveFirst,

    /// <summary>Exported unless someone denies it.</summary>
    ExportAll
}
