// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Sensors.Nfc;

public enum NfcTagSource
{
    Primary = 0,

    Text = 1,

    Uri = 2,

    TagId = 3,
}

public sealed class NfcFieldBinding
{
    public NfcFieldBinding(string fieldId, NfcTagSource source = NfcTagSource.Primary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
        FieldId = fieldId;
        Source = source;
    }

    public string FieldId { get; }

    public NfcTagSource Source { get; }

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
