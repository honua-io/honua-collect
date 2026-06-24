using System.Text;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Field.Sensors.Nfc;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Field.Sensors;

public class NfcPayloadTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "asset",
        Name = "Asset",
        Sections =
        [
            new FormSection
            {
                SectionId = "main",
                Label = "Main",
                Fields =
                [
                    new FormField { FieldId = "assetId", Label = "Asset", Type = FormFieldType.Text },
                    new FormField { FieldId = "serial", Label = "Serial", Type = FormFieldType.Numeric },
                    new FormField { FieldId = "url", Label = "URL", Type = FormFieldType.Hyperlink },
                ],
            },
        ],
    };

    // ---- NDEF parsing ------------------------------------------------------

    [Fact]
    public void Parses_an_ndef_text_record()
    {
        // status byte: UTF-8, lang length 2 ("en"); then "ASSET-42".
        var lang = "en"u8;
        var text = "ASSET-42"u8;
        var payload = new byte[1 + lang.Length + text.Length];
        payload[0] = (byte)lang.Length; // UTF-8 flag bit clear
        lang.CopyTo(payload.AsSpan(1));
        text.CopyTo(payload.AsSpan(1 + lang.Length));

        var record = BuildShortRecord(tnf: 0x01, type: (byte)'T', payload, messageEnd: true);
        var tag = NdefParser.Parse(record, tagId: "04A1B2C3");

        Assert.Equal("ASSET-42", tag.Text);
        Assert.Equal("en", tag.Records[0].LanguageCode);
        Assert.Equal("04A1B2C3", tag.TagId);
    }

    [Fact]
    public void Parses_an_ndef_uri_record_with_prefix_expansion()
    {
        // URI identifier code 0x04 == "https://"; rest = "honua.io/a/42".
        var rest = "honua.io/a/42"u8;
        var payload = new byte[1 + rest.Length];
        payload[0] = 0x04;
        rest.CopyTo(payload.AsSpan(1));

        var record = BuildShortRecord(tnf: 0x01, type: (byte)'U', payload, messageEnd: true);
        var tag = NdefParser.Parse(record);

        Assert.Equal("https://honua.io/a/42", tag.Uri);
        Assert.Equal(NfcRecordKind.Uri, tag.Records[0].Kind);
    }

    [Fact]
    public void Truncated_ndef_message_throws_format_exception()
    {
        var truncated = new byte[] { 0x51, 0x01, 0x05, (byte)'T' }; // claims 5-byte payload, none present
        Assert.Throws<FormatException>(() => NdefParser.Parse(truncated));
    }

    // ---- Payload model -----------------------------------------------------

    [Fact]
    public void Primary_value_prefers_text_then_uri_then_id()
    {
        Assert.Equal("hello", NfcTagPayload.FromText("hello", tagId: "AA").PrimaryValue);
        Assert.Equal("https://x", NfcTagPayload.FromUri("https://x", tagId: "AA").PrimaryValue);
        Assert.Equal("AA", NfcTagPayload.FromId("AA").PrimaryValue);
    }

    // ---- Field binding -----------------------------------------------------

    [Fact]
    public void Tag_text_binds_into_a_text_field()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var tag = NfcTagPayload.FromText("PUMP-7");
        var binding = new NfcFieldBinding("assetId", NfcTagSource.Text);

        Assert.True(binding.Apply(tag, session));
        Assert.Equal("PUMP-7", session.GetValue("assetId"));
    }

    [Fact]
    public void Tag_value_coerces_to_a_numeric_field()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var tag = NfcTagPayload.FromText("12345");
        var binding = new NfcFieldBinding("serial", NfcTagSource.Text);

        Assert.True(binding.Apply(tag, session));
        Assert.Equal(12345d, session.GetValue("serial"));
    }

    [Fact]
    public void Non_numeric_tag_into_numeric_field_is_rejected()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var tag = NfcTagPayload.FromText("not-a-number");
        var binding = new NfcFieldBinding("serial", NfcTagSource.Text);

        Assert.False(binding.Apply(tag, session));
        Assert.Null(session.GetValue("serial"));
    }

    [Fact]
    public void Tag_id_resolves_as_a_lookup_key_without_writing()
    {
        var tag = NfcTagPayload.FromId("04A1B2C3");
        var binding = new NfcFieldBinding("assetId", NfcTagSource.TagId);

        Assert.Equal("04A1B2C3", binding.ResolveKey(tag));
    }

    [Fact]
    public void Missing_source_value_is_a_noop()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var tag = NfcTagPayload.FromText("only-text"); // no URI record
        var binding = new NfcFieldBinding("url", NfcTagSource.Uri);

        Assert.False(binding.Apply(tag, session));
        Assert.Null(session.GetValue("url"));
    }

    private static byte[] BuildShortRecord(byte tnf, byte type, ReadOnlySpan<byte> payload, bool messageEnd)
    {
        // Header: MB(0x80) | (ME?0x40) | SR(0x10) | TNF.
        byte header = (byte)(0x80 | 0x10 | tnf);
        if (messageEnd)
        {
            header |= 0x40;
        }

        var buffer = new byte[3 + 1 + payload.Length];
        buffer[0] = header;
        buffer[1] = 1; // type length
        buffer[2] = (byte)payload.Length;
        buffer[3] = type;
        payload.CopyTo(buffer.AsSpan(4));
        return buffer;
    }
}
