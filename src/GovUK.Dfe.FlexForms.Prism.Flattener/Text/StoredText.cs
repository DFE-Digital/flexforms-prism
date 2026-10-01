using System.Net;
using System.Text.RegularExpressions;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Text;

/// <summary>
/// Reverses the web front end's input sanitiser, which HTML-encodes posted values and then turns newlines into
/// <c>&lt;br&gt;</c>. The order matters: a literal "&lt;br&gt;" typed by a user was encoded before newlines were
/// replaced, so it survives as text.
/// </summary>
public static partial class StoredText
{
    public static string Decode(string stored)
    {
        if (stored.Length == 0 || (stored.IndexOf('&') < 0 && stored.IndexOf('<') < 0))
        {
            return stored;
        }

        return WebUtility.HtmlDecode(LineBreak().Replace(stored, "\n"));
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();
}
