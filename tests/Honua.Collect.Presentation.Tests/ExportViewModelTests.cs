using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Export;
using Honua.Collect.Core.Reports;
using Honua.Collect.Presentation.Export;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Tests;

public class ExportViewModelTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "field-site",
        Name = "Field Site",
        Sections =
        [
            new FormSection
            {
                SectionId = "details",
                Label = "Site details",
                Fields =
                [
                    new FormField { FieldId = "site_name", Label = "Site name", Type = FormFieldType.Text },
                    new FormField { FieldId = "status", Label = "Status", Type = FormFieldType.Text },
                ],
            },
        ],
    };

    private static FieldRecord Record(string id, string siteName, DateTimeOffset created, FieldGeoPoint? location = null) => new()
    {
        RecordId = id,
        FormId = "field-site",
        CreatedAtUtc = created,
        Status = RecordStatus.Submitted,
        Location = location,
        Values = { ["site_name"] = siteName, ["status"] = "done" },
    };

    private static FieldRecord[] TwoRecords()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return
        [
            Record("r1", "Alpha", t0, new FieldGeoPoint(10, 20)),
            Record("r2", "Beta", t0.AddHours(1)),
        ];
    }

    [Fact]
    public void Exposes_record_count_and_entitled_header_when_pro()
    {
        var vm = new ExportViewModel(Form(), TwoRecords(), CollectEdition.Pro);

        Assert.Equal(2, vm.RecordCount);
        Assert.True(vm.IsEntitled);
        Assert.True(vm.HasRecords);
        Assert.Contains("2 record", vm.Header);
    }

    [Fact]
    public void Csv_matches_core_exporter()
    {
        var form = Form();
        var records = TwoRecords();
        var vm = new ExportViewModel(form, records, CollectEdition.Pro);

        var expected = RecordExporter.Export(form, records, ExportFormat.Csv);

        var csv = vm.ExportCsv();
        Assert.Equal(expected, csv);
        // Sanity: header columns and both rows are present.
        Assert.Contains("site_name", csv);
        Assert.Contains("Alpha", csv);
        Assert.Contains("Beta", csv);
    }

    [Fact]
    public void GeoJson_matches_core_exporter()
    {
        var form = Form();
        var records = TwoRecords();
        var vm = new ExportViewModel(form, records, CollectEdition.Pro);

        var expected = RecordExporter.Export(form, records, ExportFormat.GeoJson);

        var geojson = vm.ExportGeoJson();
        Assert.Equal(expected, geojson);
        Assert.Contains("FeatureCollection", geojson);
    }

    [Fact]
    public void Report_renders_markdown_for_latest_record()
    {
        var form = Form();
        var records = TwoRecords();
        var vm = new ExportViewModel(form, records, CollectEdition.Pro);

        // The latest record (r2 / "Beta") is the one created most recently.
        Assert.Equal("r2", vm.LatestRecord!.RecordId);

        var markdown = vm.RenderLatestReport();
        var expected = new RecordReportRenderer(new CollectEntitlements(CollectEdition.Pro))
            .RenderMarkdown(form, records[1]);

        Assert.Equal(expected, markdown);
        Assert.Contains("Beta", markdown);
        Assert.DoesNotContain("Alpha", markdown);
    }

    [Fact]
    public void Not_entitled_when_community()
    {
        var vm = new ExportViewModel(Form(), TwoRecords(), CollectEdition.Community);

        Assert.False(vm.IsEntitled);
        Assert.Contains("Pro", vm.Header);
    }

    [Fact]
    public void Csv_export_throws_when_not_pro()
    {
        var vm = new ExportViewModel(Form(), TwoRecords(), CollectEdition.Community);
        Assert.Throws<FeatureNotEntitledException>(() => vm.ExportCsv());
    }

    [Fact]
    public void GeoJson_export_throws_when_not_pro()
    {
        var vm = new ExportViewModel(Form(), TwoRecords(), CollectEdition.Community);
        Assert.Throws<FeatureNotEntitledException>(() => vm.ExportGeoJson());
    }

    [Fact]
    public void Report_throws_when_not_pro()
    {
        var vm = new ExportViewModel(Form(), TwoRecords(), CollectEdition.Community);
        Assert.Throws<FeatureNotEntitledException>(() => vm.RenderLatestReport());
    }

    [Fact]
    public void Empty_set_has_no_records_and_no_latest()
    {
        var vm = new ExportViewModel(Form(), [], CollectEdition.Pro);

        Assert.Equal(0, vm.RecordCount);
        Assert.False(vm.HasRecords);
        Assert.Null(vm.LatestRecord);
        Assert.Throws<InvalidOperationException>(() => vm.RenderLatestReport());
    }
}
