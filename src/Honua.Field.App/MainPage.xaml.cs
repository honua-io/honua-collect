// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using Honua.Field.App.Services;
using Honua.Field.App.Views;
using Honua.Field.Core.Field.Forms;
using Honua.Field.Core.Field.Related;
using Honua.Field.Core.Forms;
using Honua.Field.Core.Records;
using Honua.Field.Core.Storage;
using Honua.Field.Core.Sync;
using Honua.Field.Presentation.Forms;
using Microsoft.Extensions.DependencyInjection;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Field.App;

/// <summary>
/// Home screen. Starts a capture session over the active form, navigates to the
/// dynamic <see cref="FormPage"/>, and on submit pushes the record (and any
/// captured media) to the server via <see cref="GeoServicesFeatureSync"/> over the
/// shared, auth-aware HTTP client. Collaborators come from DI.
/// </summary>
public partial class MainPage : ContentPage
{
    private readonly AppSettings _settings;
    private readonly RecordBook _book;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<GeoServicesFeatureSync> _syncFactory;
    private readonly IRelatedRecordStore _relatedStore;

    private readonly PublishedInspectionPages _inspections;
    private (Uri Server, string FormId, PublishedFormPackage Package)? _onlineForm;

    private static string CachePath(Uri server, string formId) => Path.Combine(FileSystem.AppDataDirectory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{server}\n{formId}"))) + ".form.json");

    public MainPage(
        AppSettings settings,
        RecordBook book,
        IHttpClientFactory httpClientFactory,
        Func<GeoServicesFeatureSync> syncFactory,
        IRelatedRecordStore relatedStore,
        PublishedInspectionPages inspections)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _book = book ?? throw new ArgumentNullException(nameof(book));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _syncFactory = syncFactory ?? throw new ArgumentNullException(nameof(syncFactory));
        _relatedStore = relatedStore ?? throw new ArgumentNullException(nameof(relatedStore));
        _inspections = inspections;
        InitializeComponent();
    }

    private async void OnNewInspectionClicked(object? sender, EventArgs e)
    {
        try
        {
            var server = _settings.BaseUri;
            var formId = _settings.FormId;
            var package = _onlineForm is { } online && online.Server == server && online.FormId == formId
                ? online.Package : PublishedFormPackage.LoadCached(formId, CachePath(server, formId));
            if (package is null)
            {
                StatusLabel.Text = "Download the published form before starting an inspection.";
                return;
            }

            var session = package.CreateSession(Guid.NewGuid().ToString("n"));
            await Navigation.PushAsync(_inspections.Create(package, session, server, message => StatusLabel.Text = message));
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Could not open inspection: {ex.Message}";
        }
    }

    private async void OnSampleInspectionClicked(object? sender, EventArgs e)
    {
        var session = FormSession.CreateForNewRecord(SampleForms.FieldSite(), Guid.NewGuid().ToString("n"));
        var viewModel = new FormPageViewModel(session);
        viewModel.SubmitSucceeded += (_, record) => OnSubmitted(session, record);
        await Navigation.PushAsync(new FormPage(viewModel, _httpClientFactory, _relatedStore, _book));
    }

    private async void OnCaptureKitClicked(object? sender, EventArgs e)
    {
        var session = FormSession.CreateForNewRecord(SampleForms.CaptureKit(), Guid.NewGuid().ToString("n"));
        var viewModel = new FormPageViewModel(session);
        viewModel.SubmitSucceeded += (_, record) =>
            StatusLabel.Text = $"Capture kit: {record.Media.Count} attachment(s), asset tag “{record.Values.GetValueOrDefault("asset_tag")}”.";
        await Navigation.PushAsync(new FormPage(viewModel, _httpClientFactory, _relatedStore, _book));
    }

    private async void OnSmartFormClicked(object? sender, EventArgs e)
    {
        var session = FormSession.CreateForNewRecord(SampleForms.SmartForm(), Guid.NewGuid().ToString("n"));
        var viewModel = new FormPageViewModel(session);
        viewModel.SubmitSucceeded += (_, record) =>
            StatusLabel.Text = $"Smart form submitted — total {record.Values.GetValueOrDefault("total")}.";
        await Navigation.PushAsync(new FormPage(viewModel, _httpClientFactory, _relatedStore, _book));
    }

    private async void OnDownloadFormClicked(object? sender, EventArgs e)
    {
        StatusLabel.Text = "Downloading latest form…";
        try
        {
            using var http = _httpClientFactory.CreateClient(MauiProgram.ServerHttpClient);
            var server = http.BaseAddress ?? throw new InvalidOperationException("The form server is unavailable.");
            var formId = _settings.FormId;
            var form = await new FormPackageClient(http).DownloadPublishedAsync(formId, CachePath(server, formId));
            _onlineForm = form.OfflineEnabled ? null : (server, formId, form);
            StatusLabel.Text = $"Loaded {form.Definition.Name} (version {form.Version}).";
        }
        catch (Exception ex)
        {
            _onlineForm = null;
            StatusLabel.Text = $"Could not download form: {ex.Message}";
        }
    }

    private async void OnSubmitted(FormSession session, FieldRecord record)
    {
        // Stamp a capture location (the layer is point geometry), track it in the
        // Outbox, and push to the server.
        if (session.VisibleFields.Select(field => field.Value).OfType<FieldGeoPoint>().FirstOrDefault() is { } point)
        {
            record.Location ??= point;
        }
        FieldRecordEntry? entry = null;
        try
        {
            entry = await _book.AddSubmittedAsync(record);
            if (Microsoft.Maui.Networking.Connectivity.Current.NetworkAccess == Microsoft.Maui.Networking.NetworkAccess.None)
            {
                StatusLabel.Text = "Saved to the Outbox. Sync when a network connection is available.";
                return;
            }

            await entry.PrepareOrdinaryAddAsync(_book.SaveAsync);
            StatusLabel.Text = "Submitting to server…";

            var sync = _syncFactory();
            var result = await sync.SubmitAsync(record, _settings.Target);
            var synced = entry.ApplyOrdinaryAddResult(result);
            await _book.SaveAsync(entry);
            if (synced)
            {
                await UploadAttachmentsAsync(session, sync, result.ObjectId);
                StatusLabel.Text = $"Synced to server — objectId {result.ObjectId}. See the Records tab.";
            }
            else
            {
                StatusLabel.Text = entry.LastError ?? "See the record in the Outbox.";
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = entry?.RequiresServerVerification == true
                ? $"Check the record in the Outbox before retrying. {ex.Message}"
                : $"Could not finish saving or uploading: {ex.Message}";
        }
    }

    private async Task UploadAttachmentsAsync(FormSession session, GeoServicesFeatureSync sync, long? objectId)
    {
        if (objectId is not { } featureId)
        {
            return;
        }

        var media = session.Fields.SelectMany(f => f.Media).ToList();
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
