using Honua.Collect.App.Services;
using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Storage;
using Honua.Collect.Presentation.Export;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace Honua.Collect.App.Views;

/// <summary>
/// The Pro reporting + bulk-export screen (BACKLOG R1/R2). Shows the captured
/// record count and offers CSV / GeoJSON export and a per-record report for the
/// latest record. Export text is produced by the tested
/// <see cref="ExportViewModel"/> over the Core exporter/renderer; this page keeps
/// only the file-writing and native share-sheet glue.
/// </summary>
public partial class ExportPage : ContentPage
{
    // The edition this build runs as. Flip to Community to see the gated state;
    // Pro unlocks reporting and bulk export.
    private const CollectEdition Edition = CollectEdition.Pro;

    private readonly RecordBook _book = ServiceHelper.Get<RecordBook>();

    public ExportPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _book.InitializeAsync();
        BindingContext = new ExportViewModel(
            SampleForms.FieldSite(),
            _book.All.Select(e => e.Record),
            Edition);
    }

    private async void OnExportCsv(object? sender, EventArgs e) =>
        await ShareTextAsync(vm => vm.ExportCsv(), "csv", "text/csv", "CSV export");

    private async void OnExportGeoJson(object? sender, EventArgs e) =>
        await ShareTextAsync(vm => vm.ExportGeoJson(), "geojson", "application/geo+json", "GeoJSON export");

    private async void OnReportLatest(object? sender, EventArgs e) =>
        await ShareTextAsync(vm => vm.RenderLatestReport(), "md", "text/markdown", "Record report");

    private async Task ShareTextAsync(Func<ExportViewModel, string> produce, string extension, string contentType, string title)
    {
        if (BindingContext is not ExportViewModel vm)
        {
            return;
        }

        if (!vm.HasRecords)
        {
            StatusLabel.Text = "No records to export yet — capture one first.";
            return;
        }

        try
        {
            var text = produce(vm);
            var path = Path.Combine(FileSystem.CacheDirectory, $"{vm.FileBaseName}.{extension}");
            await File.WriteAllTextAsync(path, text);

            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = title,
                File = new ShareFile(path, contentType),
            });

            StatusLabel.Text = $"{title} ready — {text.Length} characters shared.";
        }
        catch (FeatureNotEntitledException)
        {
            StatusLabel.Text = "Reporting and bulk export require the Pro edition.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"{title} failed: {ex.Message}";
        }
    }
}
