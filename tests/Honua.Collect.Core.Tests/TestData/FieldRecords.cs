// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.TestData;

internal static class FieldRecords
{
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

    public static FieldRecord WithValues(string id, params (string Key, object? Value)[] values)
        => Create(id, values: values);
}
