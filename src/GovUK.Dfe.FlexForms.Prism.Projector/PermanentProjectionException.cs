namespace GovUK.Dfe.FlexForms.Prism.Projector;

/// <summary>
/// A projection failure that retrying cannot fix. The message should be dead-lettered with
/// <see cref="Reason"/> as the dead-letter reason.
/// </summary>
public sealed class PermanentProjectionException(string reason, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Reason { get; } = reason;
}

public static class PermanentFailureReasons
{
    public const string UnsupportedContract = "unsupported_contract";
    public const string InvalidMessage = "invalid_message";
    public const string SourceNotFound = "source_not_found";
    public const string SourceRejected = "source_rejected";
    public const string SourceMismatch = "source_mismatch";
    public const string UnreadableTemplate = "unreadable_template";
    public const string UnreadableResponse = "unreadable_response";
}
