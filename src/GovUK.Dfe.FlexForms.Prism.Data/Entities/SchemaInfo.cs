using GovUK.Dfe.FlexForms.Prism.Flattener;

namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

public class SchemaInfo
{
    public const string ContractVersionKey = "contract_version";
    public const int CurrentContractVersion = PrismVersions.ContractVersion;

    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
