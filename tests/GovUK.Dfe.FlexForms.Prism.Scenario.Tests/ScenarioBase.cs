using GovUK.Dfe.FlexForms.Prism.Data.Tests;

namespace GovUK.Dfe.FlexForms.Prism.Scenario.Tests;

/// <summary>Gives every test its own tenant, with the scenario template's fields classified.</summary>
[Collection(SqlServerCollection.Name)]
public abstract class ScenarioBase(SqlServerFixture sql) : IAsyncLifetime
{
    internal ScenarioHarness Prism { get; } = new(sql);

    internal FakeSource Source => Prism.Source;

    protected static string Named(string name) => FakeSource.Body(("name", name), ("pupils", "120"), ("secret", "do not export"));

    protected static string? NameIn(IEnumerable<FactRow> facts) => facts.SingleOrDefault(f => f.FieldId == "name")?.Value;

    public virtual Task InitializeAsync() => Prism.ClassifyAsync();

    public Task DisposeAsync() => Task.CompletedTask;
}
