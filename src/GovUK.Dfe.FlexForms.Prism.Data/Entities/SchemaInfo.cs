namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

public class SchemaInfo
{
    public const string ContractVersionKey = "contract_version";
    public const int CurrentContractVersion = 1;

    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
