namespace GovUK.Dfe.FlexForms.Prism.Flattener.Tests;

internal static class Fixtures
{
    public static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath));

    /// <summary>The fixture's path in the source tree, used to rewrite golden files.</summary>
    public static string SourcePath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GovUK.Dfe.FlexForms.Prism.Flattener.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("Could not find the test project directory.")
            : Path.Combine(directory.FullName, "Fixtures", relativePath);
    }
}
