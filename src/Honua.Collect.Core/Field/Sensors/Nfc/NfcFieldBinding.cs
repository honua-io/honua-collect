using System.Globalization;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Sensors.Nfc;

/// <summary>Which part of an <see cref="NfcTagPayload"/> a binding reads.</summary>
public enum NfcTagSource
{
    /// <summary>The payload's primary value (text, else URI, else tag id).</summary>
    Primary = 0,

    /// <summary>The first text record.</summary>
    Text = 1,

    /// <summary>The first URI record.</summary>
    Uri = 2,

    /// <summary>The tag's hardware id (UID), the natural lookup key.</summary>
    TagId = 3,
}

/// <summary>
/// Binds a parsed NFC tag to a form field or a lookup key (BACKLOG I2). Parse-only
/// and platform-neutral: a real reader produces the <see cref="NfcTagPayload"/>;
/// this maps a chosen part of it into a field with type coercion, exactly the way
/// <see cref="SensorFieldBinding"/> binds a sensor channel. The same string is the
/// natural key for a record lookup (asset id from a tag), so callers can read
/// <see cref="ResolveKey"/> without touching the session.
/// </summary>
public sealed class NfcFieldBinding
{
    /// <summary>Creates a binding.</summary>
    /// <param name="fieldId">Target field id.</param>
    /// <param name="source">Which part of the tag to read.</param>
    public NfcFieldBinding(string fieldId, NfcTagSource source = NfcTagSource.Primary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
        FieldId = fieldId;
        Source = source;
    }

    /// <summary>The target field id.</summary>
    public string FieldId { get; }

    /// <summary>The part of the tag this binding reads.</summary>
    public NfcTagSource Source { get; }

    /// <summary>
    /// Resolves the lookup key this tag yields under the binding's
    /// <see cref="Source"/>, without writing anything. Use this to key a record
    /// search (for example "find the asset whose id is on this tag").
    /// </summary>
    /// <param name="payload">The parsed tag payload.</param>
    /// <returns>The key, or <see langword="null"/> when the chosen part is absent.</returns>
    public string? ResolveKey(NfcTagPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Source switch
        {
            NfcTagSource.Primary => payload.PrimaryValue,
            NfcTagSource.Text => payload.Text,
            NfcTagSource.Uri => payload.Uri,
            NfcTagSource.TagId => payload.TagId,
            _ => null,
        };
    }

    /// <summary>
    /// Writes the tag's value into the bound field, coercing to the field's type.
    /// A tag with no value for the chosen source is a no-op (returns false).
    /// </summary>
    /// <param name="payload">The parsed tag payload.</param>
    /// <param name="session">The form session to write into.</param>
    /// <returns><see langword="true"/> if a value was written.</returns>
    public bool Apply(NfcTagPayload payload, FormSession session)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(session);

        var key = ResolveKey(payload);
        if (key is null)
        {
            return false;
        }

        if (!TryCoerce(key, session.GetField(FieldId).Field.Type, out var value))
        {
            return false;
        }

        session.SetValue(FieldId, value);
        return true;
    }

    private static bool TryCoerce(string raw, FormFieldType type, out object? value)
    {
        switch (type)
        {
            case FormFieldType.Numeric:
                if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                {
                    value = number;
                    return true;
                }

                value = null;
                return false;

            case FormFieldType.YesNo:
                if (bool.TryParse(raw, out var flag))
                {
                    value = flag;
                    return true;
                }

                value = null;
                return false;

            default:
                // Text, Hyperlink, Barcode, RecordLink, SingleChoice, etc. take the raw string.
                value = raw;
                return true;
        }
    }
}
