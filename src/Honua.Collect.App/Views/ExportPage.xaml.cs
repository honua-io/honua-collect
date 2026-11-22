// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Storage;
using Honua.Collect.Presentation.Export;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace Honua.Collect.App.Views;

public partial class ExportPage : ContentPage
{
    private readonly RecordBook _book;

    public ExportPage(RecordBook book)
    {
        _book = book ?? throw new ArgumentNullException(nameof(book));
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _book.InitializeAsync();
        BindingContext = new ExportViewModel(
            SampleForms.FieldSite(),
            _book.All.Select(e => e.Record));
    }

    private async void OnExportCsv(object? sender, EventArgs e) =>
        await ShareTextAsync(vm => vm.ExportCsv(), "csv", "text/csv", "CSV export");

    private async void OnExportGeoJson(object? sender, EventArgs e) =>
        await ShareTextAsync(vm => vm.ExportGeoJson(), "geojson", "application/geo+json", "GeoJSON export");

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
        catch (Exception ex)
        {
            StatusLabel.Text = $"{title} failed: {ex.Message}";
        }
    }
}
