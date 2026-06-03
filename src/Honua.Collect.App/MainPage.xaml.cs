using Honua.Collect.App.Services;
using Honua.Collect.App.Views;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Forms;
using Honua.Collect.Core.Storage;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Forms;
using Microsoft.Extensions.DependencyInjection;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.App;

/// <summary>
/// Home screen. Starts a capture session over the active form, navigates to the
/// dynamic <see cref="FormPage"/>, and on submit pushes the record (and any
/// captured media) to the server via <see cref="GeoServicesFeatureSync"/> over the
/// shared, auth-aware HTTP client. Collaborators come from DI.
/// </summary>
public partial class MainPage : ContentPage
{
    private readonly AppSettings _settings = ServiceHelper.Get<AppSettings>();
    private readonly RecordBook _book = ServiceHelper.Get<RecordBook>();
    private readonly HttpClient _http =
        ServiceHelper.Get<IHttpClientFactory>().CreateClient(MauiProgram.ServerHttpClient);

    private FormDefinition _activeForm = SampleForms.FieldSite();
    private FormSession? _session;

    public MainPage()
    {
        InitializeComponent();
    }

    private async void OnNewInspectionClicked(object? sender, EventArgs e)
    {
        _session = FormSession.CreateForNewRecord(_activeForm, Guid.NewGuid().ToString("n"));
        var viewModel = new FormPageViewModel(_session);
        viewModel.SubmitSucceeded += OnSubmitted;
        await Navigation.PushAsync(new FormPage(viewModel));
    }

    private async void OnCaptureKitClicked(object? sender, EventArgs e)
    {
        var session = FormSession.CreateForNewRecord(SampleForms.CaptureKit(), Guid.NewGuid().ToString("n"));
        var viewModel = new FormPageViewModel(session);
        viewModel.SubmitSucceeded += (_, record) =>
            StatusLabel.Text = $"Capture kit: {record.Media.Count} attachment(s), asset tag “{record.Values.GetValueOrDefault("asset_tag")}”.";
        await Navigation.PushAsync(new FormPage(viewModel));
    }

    private async void OnSmartFormClicked(object? sender, EventArgs e)
    {
        var session = FormSession.CreateForNewRecord(SampleForms.SmartForm(), Guid.NewGuid().ToString("n"));
        var viewModel = new FormPageViewModel(session);
        viewModel.SubmitSucceeded += (_, record) =>
            StatusLabel.Text = $"Smart form submitted — total {record.Values.GetValueOrDefault("total")}.";
        await Navigation.PushAsync(new FormPage(viewModel));
    }

    private async void OnDownloadFormClicked(object? sender, EventArgs e)
    {
        StatusLabel.Text = "Downloading latest form…";
        try
        {
            var form = await new FormPackageClient(_http).DownloadAsync(_settings.ServiceId, "field_site");
            _activeForm = form;
            StatusLabel.Text = $"Loaded form “{form.Name}” ({form.Sections.Count} section(s)). Start a new inspection.";
        }
        catch (Exception ex)
        {
            // Fall back to the bundled sample form so capture still works offline.
            _activeForm = SampleForms.FieldSite();
            StatusLabel.Text = $"Could not download form ({ex.Message}). Using the bundled sample form.";
        }
    }

    private async void OnSubmitted(object? sender, FieldRecord record)
    {
        // Stamp a capture location (the layer is point geometry), track it in the
        // Outbox, and push to the server.
        record.Location = new FieldGeoPoint(21.31, -157.81);
        var entry = await _book.AddSubmittedAsync(record);
        entry.MarkUploading();
        await _book.SaveAsync(entry);
        StatusLabel.Text = "Submitting to server…";
        try
        {
            var sync = new GeoServicesFeatureSync(_http);
            var result = await sync.SubmitAsync(record, _settings.Target);
            if (result.Success)
            {
                entry.MarkSynced(result.ObjectId?.ToString());
                await _book.SaveAsync(entry);
                await UploadAttachmentsAsync(sync, result.ObjectId);
                StatusLabel.Text = $"Synced to server — objectId {result.ObjectId}. See the Records tab.";
            }
            else
            {
                entry.MarkFailed(result.Error ?? "Sync failed.");
                await _book.SaveAsync(entry);
                StatusLabel.Text = $"Sync failed: {result.Error} (kept in Outbox).";
            }
        }
        catch (Exception ex)
        {
            entry.MarkFailed(ex.Message);
            await _book.SaveAsync(entry);
            StatusLabel.Text = $"Sync error: {ex.Message} (kept in Outbox).";
        }
    }

    private async Task UploadAttachmentsAsync(GeoServicesFeatureSync sync, long? objectId)
    {
        if (_session is null || objectId is not { } featureId)
        {
            return;
        }

        var media = _session.Fields.SelectMany(f => f.Media).ToList();
        for (var i = 0; i < media.Count; i++)
        {
            var attachment = media[i];
            var result = await sync.AddAttachmentAsync(
                featureId, attachment.LocalPath, attachment.ContentType, _settings.Target);
            StatusLabel.Text = result.Success
                ? $"Uploaded attachment {i + 1}/{media.Count} (id {result.ObjectId})."
                : $"Attachment {i + 1}/{media.Count} failed: {result.Error}";
        }
    }
}
