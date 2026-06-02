using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Capture;

/// <summary>
/// View-model for a barcode / QR scan field (BACKLOG C6). The scanner widget
/// binds to this; a successful scan writes a portable
/// <see cref="FieldBarcodeValue"/> (decoded value + format + scan time) onto the
/// field so it validates, exports, and syncs like any other value.
/// </summary>
public sealed class BarcodeCaptureField
{
    private readonly ICaptureHost _host;

    /// <summary>Binds a barcode widget to a field on a capture host.</summary>
    /// <param name="host">The form session or repeat row that owns the field.</param>
    /// <param name="field">The barcode field definition.</param>
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

    /// <summary>The barcode field definition.</summary>
    public FormField Field { get; }

    /// <summary>The current scanned value, when any.</summary>
    public FieldBarcodeValue? Current => _host.GetField(Field.FieldId).Value as FieldBarcodeValue;

    /// <summary>Whether a value has been scanned.</summary>
    public bool HasValue => Current is not null;

    /// <summary>Records a scan result on the field.</summary>
    /// <param name="value">Decoded barcode or QR value.</param>
    /// <param name="format">Symbology, such as <c>QR_CODE</c> or <c>CODE_128</c>, when known.</param>
    /// <param name="scannedAtUtc">Optional scan time.</param>
    /// <returns>The barcode value written to the field.</returns>
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

    /// <summary>Clears the scanned value.</summary>
    public void Clear() => _host.SetValue(Field.FieldId, null);
}
