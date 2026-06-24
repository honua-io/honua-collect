using System.IO.Compression;
using System.Text;
using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Reports;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Reports;

public class ReportArchiveTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "pole",
        Name = "Pole inspection",
        Sections =
        [
            new FormSection
            {
                SectionId = "header",
                Label = "Header",
                Fields = [new FormField { FieldId = "poleId", Label = "Pole ID", Type = FormFieldType.Text }],
            },
        ],
    };

    private static FieldRecord Record(string id, string poleId)
    {
        var r = new FieldRecord { RecordId = id, FormId = "pole", Status = RecordStatus.Submitted };
        r.Values["poleId"] = poleId;
        return r;
    }

    private static ReportManifest Manifest(params (string Id, string Pole)[] records)
        => new BulkReportGenerator(new CollectEntitlements(CollectEdition.Pro))
            .Generate(Form(), records.Select(r => Record(r.Id, r.Pole)), fileNameTemplate: "pole-{poleId}");

    private static ZipArchive Open(byte[] zip)
        => new(new MemoryStream(zip), ZipArchiveMode.Read);

    [Fact]
    public void Bundle_contains_one_entry_per_manifest_entry_with_matching_names_and_content()
    {
        var manifest = Manifest(("r1", "P-1"), ("r2", "P-2"));

        using var zip = Open(ReportArchive.Bundle(manifest));

        Assert.Equal(
            manifest.Entries.Select(e => e.FileName).OrderBy(n => n),
            zip.Entries.Select(e => e.FullName).OrderBy(n => n));

        foreach (var source in manifest.Entries)
        {
            var entry = zip.GetEntry(source.FileName)!;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var expected = Encoding.UTF8.GetString(source.Content);
            Assert.Equal(expected, reader.ReadToEnd());
        }
    }

    [Fact]
    public void Bundle_uses_the_manifest_file_names_including_extension()
    {
        using var zip = Open(ReportArchive.Bundle(Manifest(("r1", "P-1"))));

        Assert.Equal("pole-P-1.md", zip.Entries.Single().FullName);
    }

    [Fact]
    public void Single_entry_manifest_bundles_to_one_file()
    {
        using var zip = Open(ReportArchive.Bundle(Manifest(("solo", "P-9"))));
        Assert.Single(zip.Entries);
    }

    [Fact]
    public void Empty_manifest_yields_a_valid_empty_zip()
    {
        var bytes = ReportArchive.Bundle(Manifest());

        using var zip = Open(bytes);
        Assert.Empty(zip.Entries);
    }

    [Fact]
    public void Bundle_is_deterministic_for_identical_input()
    {
        var first = ReportArchive.Bundle(Manifest(("r1", "P-1"), ("r2", "P-2")));
        var second = ReportArchive.Bundle(Manifest(("r1", "P-1"), ("r2", "P-2")));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Bundle_guards_null_manifest()
        => Assert.Throws<ArgumentNullException>(() => ReportArchive.Bundle(null!));
}
