// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Capture;

public sealed class BarcodeCaptureField
{
    private readonly ICaptureHost _host;

    public BarcodeCaptureField(ICaptureHost host, FormField field)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(field);

        if (field.Type != FormFieldType.Barcode)
        {
            throw new ArgumentException($"Field '{field.FieldId}' is {field.Type}, not a Barcode field.", nameof(field));
        }

        _host = host;
        Field = field;
    }

    public FormField Field { get; }

    public FieldBarcodeValue? Current => _host.GetField(Field.FieldId).Value as FieldBarcodeValue;

    public bool HasValue => Current is not null;

    public FieldBarcodeValue Scan(string value, string? format = null, DateTimeOffset? scannedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var barcode = new FieldBarcodeValue
        {
            Value = value,
            Format = format,
            ScannedAtUtc = scannedAtUtc ?? DateTimeOffset.UtcNow,
        };

        _host.SetValue(Field.FieldId, barcode);
        return barcode;
    }

    public void Clear() => _host.SetValue(Field.FieldId, null);
}
