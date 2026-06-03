using Honua.Collect.App.Views;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Forms;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.App;

/// <summary>
/// Home screen. Starts a new capture session over the active form, navigates to
/// the dynamic <see cref="FormPage"/>, and on submit pushes the record (and any
/// captured media) to the server's GeoServices Feature Server via
/// <see cref="GeoServicesFeatureSync"/>.
/// </summary>
public partial class MainPage : ContentPage
{
    // Android emulator reaches the host loopback at 10.0.2.2. Demo credentials.
    private static readonly HttpClient Http = CreateClient();
    private static readonly GeoServicesTarget Target = new("http://10.0.2.2:18080", "mobile_offline_demo", 68910);

    // The form used for new inspections — the bundled sample until a fresh
    // definition is downloaded from the server's FormServer.
    private FormDefinition _activeForm = SampleForms.FieldSite();

    // The session currently being captured, so the submit handler can read its
    // captured media (with local file paths) for attachment upload.
    private FormSession? _session;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri("http://10.0.2.2:18080") };
        http.DefaultRequestHeaders.Add("X-API-Key", "AdminPass123!");
        return http;
    }

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
        // A local-only demo of every capture widget; not synced to the server.
        var session = FormSession.CreateForNewRecord(SampleForms.CaptureKit(), Guid.NewGuid().ToString("n"));
        var viewModel = new FormPageViewModel(session);
        viewModel.SubmitSucceeded += (_, record) =>
            StatusLabel.Text = $"Capture kit: {record.Media.Count} attachment(s), asset tag “{record.Values.GetValueOrDefault("asset_tag")}”.";
        await Navigation.PushAsync(new FormPage(viewModel));
    }

    private async void OnDownloadFormClicked(object? sender, EventArgs e)
    {
        StatusLabel.Text = "Downloading latest form…";
        try
        {
            var form = await new FormPackageClient(Http).DownloadAsync("mobile_offline_demo", "field_site");
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
        var entry = CaptureStore.AddSubmitted(record);
        entry.MarkUploading();
        await CaptureStore.SaveAsync(entry);
        StatusLabel.Text = "Submitting to server…";
        try
        {
            var sync = new GeoServicesFeatureSync(Http);
            var result = await sync.SubmitAsync(record, Target);
            if (result.Success)
            {
                entry.MarkSynced(result.ObjectId?.ToString());
                await CaptureStore.SaveAsync(entry);
                await UploadAttachmentsAsync(sync, result.ObjectId);
                StatusLabel.Text = $"Synced to server — objectId {result.ObjectId}. See the Records tab.";
            }
            else
            {
                entry.MarkFailed(result.Error ?? "Sync failed.");
                await CaptureStore.SaveAsync(entry);
                StatusLabel.Text = $"Sync failed: {result.Error} (kept in Outbox).";
            }
        }
        catch (Exception ex)
        {
            entry.MarkFailed(ex.Message);
            await CaptureStore.SaveAsync(entry);
            StatusLabel.Text = $"Sync error: {ex.Message} (kept in Outbox).";
        }
    }

    private async Task UploadAttachmentsAsync(GeoServicesFeatureSync sync, long? objectId)
    {
        if (_session is null || objectId is not { } featureId)
        {
            return;
        }

        // Captured media carries the local file path; upload each to the new feature.
        var media = _session.Fields.SelectMany(f => f.Media).ToList();
        for (var i = 0; i < media.Count; i++)
        {
            var attachment = media[i];
            var result = await sync.AddAttachmentAsync(
                featureId, attachment.LocalPath, attachment.ContentType, Target);
            StatusLabel.Text = result.Success
                ? $"Uploaded attachment {i + 1}/{media.Count} (id {result.ObjectId})."
                : $"Attachment {i + 1}/{media.Count} failed: {result.Error}";
        }
    }
}
