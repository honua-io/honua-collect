using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.TestData;

/// <summary>
/// Shared builders for <see cref="FieldRecord"/> test fixtures. Several suites
/// were each carrying a private <c>Record(id, values…)</c> helper that built the
/// same shape; this centralizes it so the construction is defined once.
/// </summary>
internal static class FieldRecords
{
    /// <summary>
    /// Builds a record with the given id and ad-hoc attribute values.
    /// </summary>
    /// <param name="id">The record id.</param>
    /// <param name="formId">The owning form id (defaults to <c>"f"</c>).</param>
    /// <param name="status">The record status (defaults to <see cref="RecordStatus.Draft"/>).</param>
    /// <param name="location">Optional capture location.</param>
    /// <param name="values">Attribute key/value pairs to set on the record.</param>
    public static FieldRecord Create(
        string id,
        string formId = "f",
        RecordStatus status = RecordStatus.Draft,
        FieldGeoPoint? location = null,
        params (string Key, object? Value)[] values)
    {
        var record = new FieldRecord
        {
            RecordId = id,
            FormId = formId,
            Status = status,
            Location = location,
        };

        foreach (var (key, value) in values)
        {
            record.Values[key] = value;
        }

        return record;
    }

    /// <summary>Builds a record with attribute values and the default form/status.</summary>
    /// <param name="id">The record id.</param>
    /// <param name="values">Attribute key/value pairs to set on the record.</param>
    public static FieldRecord WithValues(string id, params (string Key, object? Value)[] values)
        => Create(id, values: values);
}
