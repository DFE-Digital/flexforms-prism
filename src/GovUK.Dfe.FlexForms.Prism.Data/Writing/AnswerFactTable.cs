using System.Data;
using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;

namespace GovUK.Dfe.FlexForms.Prism.Data.Writing;

/// <summary>
/// Shapes facts into a <see cref="DataTable"/> whose column names match <c>prism.answer_facts</c>.
/// </summary>
internal static class AnswerFactTable
{
    public static DataTable Create()
    {
        var table = new DataTable("answer_facts") { Locale = System.Globalization.CultureInfo.InvariantCulture };
        table.Columns.Add("generation_id", typeof(Guid));
        table.Columns.Add("tenant_id", typeof(Guid));
        table.Columns.Add("application_id", typeof(Guid));
        table.Columns.Add("logical_key_hash", typeof(byte[]));
        table.Columns.Add("field_id", typeof(string));
        table.Columns.Add("parent_field_id", typeof(string));
        table.Columns.Add("occurrence_path", typeof(string));
        table.Columns.Add("item_id", typeof(string));
        table.Columns.Add("item_ordinal", typeof(int));
        table.Columns.Add("nested_path", typeof(string));
        table.Columns.Add("data_type", typeof(string));
        table.Columns.Add("is_completed", typeof(bool));
        table.Columns.Add("interpretation_status", typeof(string));
        table.Columns.Add("value_string", typeof(string));
        table.Columns.Add("value_decimal", typeof(decimal));
        table.Columns.Add("value_bool", typeof(bool));
        table.Columns.Add("value_date", typeof(DateTime));
        table.Columns.Add("value_date_time", typeof(DateTime));
        table.Columns.Add("value_json", typeof(string));
        table.Columns.Add("raw_value", typeof(string));
        return table;
    }

    public static void AddRow(DataTable table, Guid generationId, Guid tenantId, Guid applicationId, AnswerFact fact)
    {
        table.Rows.Add(
            generationId,
            tenantId,
            applicationId,
            fact.LogicalKeyHash,
            fact.FieldId,
            (object?)fact.ParentFieldId ?? DBNull.Value,
            fact.OccurrencePath,
            (object?)fact.ItemId ?? DBNull.Value,
            (object?)fact.ItemOrdinal ?? DBNull.Value,
            fact.NestedPath,
            (object?)fact.DataType ?? DBNull.Value,
            (object?)fact.IsCompleted ?? DBNull.Value,
            fact.InterpretationStatus.ToString(),
            (object?)fact.ValueString ?? DBNull.Value,
            (object?)fact.ValueDecimal ?? DBNull.Value,
            (object?)fact.ValueBool ?? DBNull.Value,
            fact.ValueDate is { } date ? date.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
            (object?)fact.ValueDateTime ?? DBNull.Value,
            (object?)fact.ValueJson ?? DBNull.Value,
            (object?)fact.RawValue ?? DBNull.Value);
    }
}
