namespace GovUK.Dfe.FlexForms.Prism.Source;

/// <summary>A source API call failed.</summary>
public abstract class SourceException : Exception
{
    protected SourceException(string message, int? statusCode, Exception? innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public int? StatusCode { get; }

    /// <summary>Whether retrying the same call later may succeed.</summary>
    public abstract bool IsTransient { get; }
}

/// <summary>The source is unavailable or overloaded, or rejected our credentials; retry later.</summary>
public sealed class SourceUnavailableException(string message, int? statusCode = null, Exception? innerException = null)
    : SourceException(message, statusCode, innerException)
{
    public override bool IsTransient => true;
}

/// <summary>The requested resource does not exist for the tenant.</summary>
public sealed class SourceNotFoundException(string message, Exception? innerException = null)
    : SourceException(message, 404, innerException)
{
    public override bool IsTransient => false;
}

/// <summary>The source rejected the request or returned something we can't read; retrying won't help.</summary>
public sealed class SourceRejectedException(string message, int? statusCode = null, Exception? innerException = null)
    : SourceException(message, statusCode, innerException)
{
    public override bool IsTransient => false;
}
