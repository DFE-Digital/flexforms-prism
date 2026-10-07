using System.Globalization;
using System.Text;
using GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;
using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;
using GovUK.Dfe.FlexForms.Prism.Flattener.Flattening;
using GovUK.Dfe.FlexForms.Prism.Flattener.Responses;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Tests;

/// <summary>
/// Golden files for the real Transfer template and response. Any change to flattening output shows up as a diff
/// in review. Regenerate with <c>UPDATE_GOLDEN=1 dotnet test</c> and bump the projector version if the change
/// is intentional.
/// </summary>
public class TransferGoldenTests
{
    private static readonly Guid TemplateVersionId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static readonly TemplateCatalogue Catalogue = TemplateCatalogueBuilder.Build(Fixtures.Read("transfer/template.json"));

    private static readonly FlattenResult Result = ResponseFlattener.Flatten(
        Catalogue,
        ResponseParser.Parse(Fixtures.Read("transfer/response.json")),
        TestSupport.AllowAll(Catalogue));

    [Fact]
    public void Catalogue_matches_golden_file() => AssertGolden("transfer/catalogue.golden.tsv", RenderCatalogue());

    [Fact]
    public void Facts_match_golden_file() => AssertGolden("transfer/facts.golden.tsv", RenderFacts());

    [Fact]
    public void Every_answer_is_accounted_for()
    {
        Assert.Equal(0, Result.WithheldAnswerCount);
        Assert.DoesNotContain(Result.Warnings, w => w.Code is FlattenWarningCodes.UnknownKey
            or FlattenWarningCodes.UnknownNestedField
            or FlattenWarningCodes.MalformedCollection
            or FlattenWarningCodes.MalformedCollectionItem
            or FlattenWarningCodes.DuplicateLogicalKey);
        Assert.DoesNotContain(Result.Facts, f => f.InterpretationStatus is InterpretationStatus.ParseFailed or InterpretationStatus.Unsupported);
    }

    [Fact]
    public void Derived_items_use_the_per_item_copy_not_the_stale_top_level_copy()
    {
        var chair = Assert.Single(Result.Facts, f => f.FieldId == "chairName-joining");

        Assert.Equal("trustDeclarations/webster-primary-school", chair.OccurrencePath);
        Assert.Equal("dfsdf55555555", chair.ValueString);
        Assert.Contains(Result.Warnings, w => w.Code == FlattenWarningCodes.NestedFieldAtTopLevel && w.Key == "chairName-joining");
    }

    private static string RenderCatalogue()
    {
        var builder = new StringBuilder();
        builder.AppendLine("order\tparent\tfield\tflow\tmode\ttask\tpage\tlabel\tdata_type\tcontrol\tcomplex\trequired\toptions");
        foreach (var f in Catalogue.Fields)
        {
            AppendRow(builder,
                f.FieldOrder.ToString(CultureInfo.InvariantCulture), f.ParentFieldId, f.FieldId, f.FlowId, f.FlowMode?.ToString(),
                f.TaskId, f.PageId, f.Label, f.DataType, f.ControlType, f.ComplexFieldId, f.IsRequired?.ToString(),
                string.Join('|', f.Options.Select(o => o.Value)));
        }

        return builder.ToString();
    }

    private static string RenderFacts()
    {
        var builder = new StringBuilder();
        var hash = FactHasher.Compute(
            new FactHashHeader(TemplateVersionId, PrismVersions.ProjectorVersion, PrismVersions.ContractVersion, 1),
            Result.Facts);
        builder.AppendLine(CultureInfo.InvariantCulture, $"# facts={Result.Facts.Count} hash={Convert.ToHexStringLower(hash)}");
        foreach (var warning in Result.Warnings.OrderBy(w => w.Code, StringComparer.Ordinal).ThenBy(w => w.Key, StringComparer.Ordinal))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"# warning {warning.Code} {warning.Key}");
        }

        builder.AppendLine("parent\toccurrence\tfield\tnested\titem_id\tordinal\tdata_type\tcompleted\tstatus\tstring\tdecimal\tbool\tdate\tdatetime\tjson\traw");
        var ordered = Result.Facts
            .OrderBy(f => f.ParentFieldId, StringComparer.Ordinal)
            .ThenBy(f => f.OccurrencePath, StringComparer.Ordinal)
            .ThenBy(f => f.FieldId, StringComparer.Ordinal)
            .ThenBy(f => f.NestedPath, StringComparer.Ordinal);
        foreach (var f in ordered)
        {
            AppendRow(builder,
                f.ParentFieldId, f.OccurrencePath, f.FieldId, f.NestedPath, f.ItemId,
                f.ItemOrdinal?.ToString(CultureInfo.InvariantCulture), f.DataType, f.IsCompleted?.ToString(),
                f.InterpretationStatus.ToString(), f.ValueString,
                f.ValueDecimal?.ToString("0.##########", CultureInfo.InvariantCulture), f.ValueBool?.ToString(),
                f.ValueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                f.ValueDateTime?.ToString("O", CultureInfo.InvariantCulture), f.ValueJson, f.RawValue);
        }

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, params string?[] cells) =>
        builder.AppendLine(string.Join('\t', cells.Select(Cell)));

    private static string Cell(string? value) =>
        value is null
            ? "\\N"
            : value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);

    private static void AssertGolden(string relativePath, string actual)
    {
        actual = actual.ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(Fixtures.SourcePath(relativePath), actual, new UTF8Encoding(false));
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath);
        Assert.True(File.Exists(path), $"Golden file {relativePath} is missing. Run the tests with UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }
}
