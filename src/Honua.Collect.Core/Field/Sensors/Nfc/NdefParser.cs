using System.Text;

namespace Honua.Collect.Core.Field.Sensors.Nfc;

/// <summary>
/// A minimal, platform-neutral NDEF message parser (BACKLOG I2, read side). It
/// decodes the two NFC Forum well-known record types a data-collection app cares
/// about — Text (RTD "T") and URI (RTD "U") — from the raw NDEF byte message a
/// platform reader pulls off a tag. Parse-only: no radio, no I/O.
/// </summary>
public static class NdefParser
{
    // NFC Forum URI Record Type Definition: identifier-code -> prefix (subset of the standard table).
    private static readonly string[] UriPrefixes =
    [
        "", "http://www.", "https://www.", "http://", "https://", "tel:", "mailto:",
        "ftp://anonymous:anonymous@", "ftp://ftp.", "ftps://", "sftp://", "smb://",
        "nfs://", "ftp://", "dav://", "news:", "telnet://", "imap:", "rtsp://",
        "urn:", "pop:", "sip:", "sips:", "tftp:", "btspp://", "btl2cap://", "btgoep://",
        "tcpobex://", "irdaobex://", "file://", "urn:epc:id:", "urn:epc:tag:",
        "urn:epc:pat:", "urn:epc:raw:", "urn:epc:", "urn:nfc:",
    ];

    // NDEF record header flags.
    private const byte MessageEnd = 0x40;   // ME
    private const byte ShortRecord = 0x10;  // SR
    private const byte TypeNameMask = 0x07; // TNF
    private const byte TnfWellKnown = 0x01;

    /// <summary>
    /// Parses an NDEF message into records. Unknown / non-well-known records are
    /// surfaced as <see cref="NfcRecordKind.Other"/> rather than dropped, so the
    /// payload still reflects the tag. Returns an empty payload for empty input.
    /// </summary>
    /// <param name="message">The raw NDEF message bytes.</param>
    /// <param name="tagId">Optional tag UID to attach.</param>
    /// <returns>The parsed payload.</returns>
    /// <exception cref="FormatException">The bytes are not a well-formed NDEF message.</exception>
    public static NfcTagPayload Parse(ReadOnlySpan<byte> message, string? tagId = null)
    {
        var records = new List<NfcRecord>();
        var offset = 0;

        while (offset < message.Length)
        {
            var header = message[offset++];
            var tnf = (byte)(header & TypeNameMask);
            var shortRecord = (header & ShortRecord) != 0;
            var messageEnd = (header & MessageEnd) != 0;

            if (offset >= message.Length)
            {
                throw new FormatException("Truncated NDEF record header.");
            }

            int typeLength = message[offset++];

            long payloadLength;
            if (shortRecord)
            {
                if (offset >= message.Length)
                {
                    throw new FormatException("Truncated NDEF short-record length.");
                }

                payloadLength = message[offset++];
            }
            else
            {
                if (offset + 4 > message.Length)
                {
                    throw new FormatException("Truncated NDEF record length.");
                }

                payloadLength = (uint)((message[offset] << 24) | (message[offset + 1] << 16)
                    | (message[offset + 2] << 8) | message[offset + 3]);
                offset += 4;
            }

            if (offset + typeLength > message.Length)
            {
                throw new FormatException("Truncated NDEF record type.");
            }

            var type = message.Slice(offset, typeLength);
            offset += typeLength;

            if (offset + payloadLength > message.Length)
            {
                throw new FormatException("Truncated NDEF record payload.");
            }

            var payload = message.Slice(offset, (int)payloadLength);
            offset += (int)payloadLength;

            records.Add(DecodeRecord(tnf, type, payload));

            if (messageEnd)
            {
                break;
            }
        }

        return new NfcTagPayload(tagId, records);
    }

    private static NfcRecord DecodeRecord(byte tnf, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        if (tnf == TnfWellKnown && type.Length == 1)
        {
            switch ((char)type[0])
            {
                case 'T':
                    return DecodeText(payload);
                case 'U':
                    return DecodeUri(payload);
            }
        }

        return new NfcRecord(NfcRecordKind.Other, Encoding.UTF8.GetString(payload));
    }

    private static NfcRecord DecodeText(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return new NfcRecord(NfcRecordKind.Text, string.Empty);
        }

        var status = payload[0];
        var langLength = status & 0x3F;
        var isUtf16 = (status & 0x80) != 0;

        if (1 + langLength > payload.Length)
        {
            throw new FormatException("NDEF text record language code overruns payload.");
        }

        var lang = Encoding.ASCII.GetString(payload.Slice(1, langLength));
        var textBytes = payload[(1 + langLength)..];
        var text = (isUtf16 ? Encoding.BigEndianUnicode : Encoding.UTF8).GetString(textBytes);
        return new NfcRecord(NfcRecordKind.Text, text, string.IsNullOrEmpty(lang) ? null : lang);
    }

    private static NfcRecord DecodeUri(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return new NfcRecord(NfcRecordKind.Uri, string.Empty);
        }

        var code = payload[0];
        var prefix = code < UriPrefixes.Length ? UriPrefixes[code] : string.Empty;
        var rest = Encoding.UTF8.GetString(payload[1..]);
        return new NfcRecord(NfcRecordKind.Uri, prefix + rest);
    }
}
