// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.App.Capture;
using Honua.Field.App.Services;
using Honua.Field.Core.Records;
using Honua.Field.Core.Enterprise;
using Honua.Field.Core.History;
using Honua.Field.Core.Storage;
using Honua.Field.Presentation.History;
using Honua.Field.Presentation.Records;

namespace Honua.Field.App.Views;

/// <summary>The Drafts/Outbox/Sent records screen, rebuilt from the record book on view.</summary>
public partial class RecordsPage : ContentPage
{
    private readonly RecordBook _book;
    private readonly PublishedInspectionPages _inspections;
    private readonly AppSettings _settings;
    private readonly PublishedOutboxReplay _publishedReplay;
    private readonly IRecordHistoryStore _historyStore;
    private readonly RecordRevertService _revertService;
    private readonly IAuthSessionStore _sessionStore;
    private readonly IDeviceAuthorization _authorization;

    public RecordsPage(
        RecordBook book,
        IRecordHistoryStore historyStore,
        RecordRevertService revertService,
        IAuthSessionStore sessionStore,
        IDeviceAuthorization authorization,
        PublishedInspectionPages inspections,
        AppSettings settings,
        PublishedOutboxReplay publishedReplay)
    {
        _book = book ?? throw new ArgumentNullException(nameof(book));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _revertService = revertService ?? throw new ArgumentNullException(nameof(revertService));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _inspections = inspections;
        _settings = settings;
        _publishedReplay = publishedReplay;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _book.InitializeAsync();
        BindingContext = new RecordBoxViewModel(_book.All);
    }

    private Task<bool> ConfirmLegacyOwnerAsync(string title, string action)
    {
        var account = _sessionStore.Current?.UserId ?? (string.IsNullOrEmpty(_sessionStore.FallbackApiKey)
            ? "anonymous access" : "the configured access key");
        return DisplayAlert(title,
            $"This inspection was saved before Field recorded its owner. Continue only if {account} is the original account or credential.",
            action, "Cancel");
    }

    private async void OnRecordSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is CollectionView view)
        {
            view.SelectedItem = null;
        }

        if (e.CurrentSelection.FirstOrDefault() is not RecordBoxItem { Record: { } row })
        {
            return;
        }

        if (row.Entry.IsPendingUpload && row.Entry.PublishedSubmission is { ReplayPrincipal: null } oldSubmission)
        {
            try
            {
                var principal = AuthHeaderHandler.CaptureReplayPrincipal(_sessionStore);
                if (!Uri.TryCreate(oldSubmission.ServerBaseUrl, UriKind.Absolute, out var source)
                    || source != _settings.BaseUri)
                {
                    await DisplayAlert("Recover saved inspection",
                        "Sign in to the original server and account to recover this inspection.", "OK");
                    return;
                }

                if (!await ConfirmLegacyOwnerAsync("Retry saved inspection", "Retry"))
                {
                    return;
                }

                var replayed = await _publishedReplay.ReplayAsync(row.Entry, CancellationToken.None, principal);
                BindingContext = new RecordBoxViewModel(_book.All);
                await DisplayAlert("Recover saved inspection",
                    replayed ? "The server confirmed this inspection." : row.Entry.LastError ?? "This inspection remains saved on this device.", "OK");
            }
            catch (Exception ex)
            {
                await DisplayAlert("Recover saved inspection", ex.Message, "OK");
            }

            return;
        }

        if (row.Entry.CanOpenPublishedEditor && row.Entry.PublishedDraft is { } draft)
        {
            try
            {
                var server = _settings.BaseUri;
                var (package, session) = draft.Open(row.Entry.Record.FormId, row.RecordId, server, CaptureFiles.MediaDirectory);
                var principal = draft.ReplayPrincipal;
                if (principal is null)
                {
                    principal = AuthHeaderHandler.CaptureReplayPrincipal(_sessionStore);
                    if (!await ConfirmLegacyOwnerAsync("Open saved inspection", "Open draft"))
                    {
                        return;
                    }
                }
                var isCorrection = draft.ReplacesRejectedSubmission || row.Entry.SyncState == Core.Records.RecordSyncState.SubmissionRejected;
                await Navigation.PushAsync(_inspections.Create(package, session, server, isCorrection: isCorrection, draftPrincipal: principal));
            }
            catch (Exception ex)
            {
                await DisplayAlert("Open draft", ex.Message, "OK");
            }

            return;
        }

        var editorId = _sessionStore.Current?.UserId;
        var canEdit = row.Entry.PublishedSubmission is null && !string.IsNullOrWhiteSpace(editorId)
            && _authorization.Can(FieldPermission.EditRecords, row.RecordId);

        var historyVm = canEdit
            ? new RecordHistoryViewModel(_historyStore, row.Entry, _revertService, editorId!)
            : new RecordHistoryViewModel(_historyStore, row.RecordId);

        await Navigation.PushAsync(new RecordHistoryPage(historyVm));
    }
}
