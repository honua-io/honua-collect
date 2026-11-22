// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Sensors.Nfc;

public enum NfcRecordKind
{
    Text = 0,

    Uri = 1,

    Id = 2,

    Other = 3,
}

public readonly record struct NfcRecord(NfcRecordKind Kind, string Value, string? LanguageCode = null);

public sealed class NfcTagPayload
{
    private readonly List<NfcRecord> _records;

    public NfcTagPayload(string? tagId, IEnumerable<NfcRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        TagId = string.IsNullOrWhiteSpace(tagId) ? null : tagId;
        _records = [.. records];
    }

    public string? TagId { get; }

    public IReadOnlyList<NfcRecord> Records => _records;

    public string? Text => First(NfcRecordKind.Text)?.Value;

    public string? Uri => First(NfcRecordKind.Uri)?.Value;

    public NfcRecord? First(NfcRecordKind kind)
    {
        foreach (var record in _records)
        {
            if (record.Kind == kind)
            {
                return record;
            }
        }

        return null;
    }

    public string? PrimaryValue => Text ?? Uri ?? TagId;

    public static NfcTagPayload FromText(string text, string? languageCode = null, string? tagId = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new NfcTagPayload(tagId, [new NfcRecord(NfcRecordKind.Text, text, languageCode)]);
    }

    public static NfcTagPayload FromUri(string uri, string? tagId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        return new NfcTagPayload(tagId, [new NfcRecord(NfcRecordKind.Uri, uri)]);
    }

    public static NfcTagPayload FromId(string tagId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagId);
        return new NfcTagPayload(tagId, [new NfcRecord(NfcRecordKind.Id, tagId)]);
    }
}
