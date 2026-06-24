namespace Honua.Collect.Core.Field.Sensors.Nfc;

/// <summary>The kind of an <see cref="NfcRecord"/>.</summary>
public enum NfcRecordKind
{
    /// <summary>A plain-text NDEF record (RTD "T").</summary>
    Text = 0,

    /// <summary>A URI NDEF record (RTD "U").</summary>
    Uri = 1,

    /// <summary>The tag's hardware identifier (UID), not an NDEF record.</summary>
    Id = 2,

    /// <summary>A record this parser does not specialise.</summary>
    Other = 3,
}

/// <summary>
/// One logical record decoded from an NFC tag (BACKLOG I2, read side). Platform-
/// neutral: no radio here — a real reader hands this parser the bytes/strings it
/// pulled off the tag, and this models the result so a tag can drive a field or a
/// lookup. Construction is via the static parse helpers on
/// <see cref="NfcTagPayload"/>.
/// </summary>
/// <param name="Kind">The record kind.</param>
/// <param name="Value">
/// The decoded value: the text body for <see cref="NfcRecordKind.Text"/>, the URI
/// for <see cref="NfcRecordKind.Uri"/>, the UID for <see cref="NfcRecordKind.Id"/>.
/// </param>
/// <param name="LanguageCode">The IANA language code for a text record, when present.</param>
public readonly record struct NfcRecord(NfcRecordKind Kind, string Value, string? LanguageCode = null);

/// <summary>
/// A parsed NFC tag payload: its hardware id plus its NDEF records (BACKLOG I2).
/// This is a parse-only model — it never touches a radio. A platform NFC reader
/// produces the raw inputs (UID bytes, NDEF text/URI records) and this turns them
/// into a structured payload that <see cref="NfcFieldBinding"/> can bind to a
/// field or a lookup key.
/// </summary>
public sealed class NfcTagPayload
{
    private readonly List<NfcRecord> _records;

    /// <summary>Creates a payload from a tag id and its records.</summary>
    /// <param name="tagId">The tag's hardware UID (hex, no separators), or null if unknown.</param>
    /// <param name="records">The NDEF records read from the tag.</param>
    public NfcTagPayload(string? tagId, IEnumerable<NfcRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        TagId = string.IsNullOrWhiteSpace(tagId) ? null : tagId;
        _records = [.. records];
    }

    /// <summary>The tag's hardware UID, or <see langword="null"/> when not read.</summary>
    public string? TagId { get; }

    /// <summary>The NDEF records in tag order.</summary>
    public IReadOnlyList<NfcRecord> Records => _records;

    /// <summary>The first text record's value, or <see langword="null"/>.</summary>
    public string? Text => First(NfcRecordKind.Text)?.Value;

    /// <summary>The first URI record's value, or <see langword="null"/>.</summary>
    public string? Uri => First(NfcRecordKind.Uri)?.Value;

    /// <summary>The first record of a kind, or <see langword="null"/>.</summary>
    /// <param name="kind">Record kind to find.</param>
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

    /// <summary>
    /// The value best suited to drive a field/lookup: the first text record, else
    /// the first URI, else the tag id. <see langword="null"/> only for an empty,
    /// id-less tag.
    /// </summary>
    public string? PrimaryValue => Text ?? Uri ?? TagId;

    /// <summary>Builds a payload carrying a single text record.</summary>
    /// <param name="text">The text body.</param>
    /// <param name="languageCode">Optional IANA language code.</param>
    /// <param name="tagId">Optional tag UID.</param>
    public static NfcTagPayload FromText(string text, string? languageCode = null, string? tagId = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new NfcTagPayload(tagId, [new NfcRecord(NfcRecordKind.Text, text, languageCode)]);
    }

    /// <summary>Builds a payload carrying a single URI record.</summary>
    /// <param name="uri">The URI.</param>
    /// <param name="tagId">Optional tag UID.</param>
    public static NfcTagPayload FromUri(string uri, string? tagId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        return new NfcTagPayload(tagId, [new NfcRecord(NfcRecordKind.Uri, uri)]);
    }

    /// <summary>Builds an id-only payload (a tag with no NDEF data).</summary>
    /// <param name="tagId">The tag UID.</param>
    public static NfcTagPayload FromId(string tagId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagId);
        return new NfcTagPayload(tagId, [new NfcRecord(NfcRecordKind.Id, tagId)]);
    }
}
