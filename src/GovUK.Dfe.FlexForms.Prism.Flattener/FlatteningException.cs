namespace GovUK.Dfe.FlexForms.Prism.Flattener;

/// <summary>
/// The input can never be flattened as it is, so retrying will not help.
/// </summary>
public abstract class FlatteningException : Exception
{
    protected FlatteningException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The template version JSON is not a readable form template.
/// </summary>
public sealed class TemplateFormatException : FlatteningException
{
    public TemplateFormatException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The response body is not a readable response.
/// </summary>
public sealed class ResponseFormatException : FlatteningException
{
    public ResponseFormatException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
