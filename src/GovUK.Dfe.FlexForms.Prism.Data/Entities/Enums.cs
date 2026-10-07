namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

public enum GenerationKind
{
    Current,
    Submission
}

public enum GenerationStatus
{
    Building,
    Active,
    Superseded
}

public enum ApplicationLifecycle
{
    Draft,
    Submitted,
    Deleted
}

public enum OperationKind
{
    Backfill,
    Reconciliation
}

public enum BackfillStatus
{
    Pending,
    Running,
    Completed,
    Cancelled,
    Failed
}
