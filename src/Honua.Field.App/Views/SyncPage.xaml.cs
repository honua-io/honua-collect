// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Honua.Field.App.Capture;
using Honua.Field.App.Services;
using Honua.Field.Core.Storage;
using Honua.Field.Core.Sync;
using Honua.Field.Presentation.Sync;
using Microsoft.Extensions.Logging;

namespace Honua.Field.App.Views;

/// <summary>
/// The offline sync center: shows the Outbox/Sent/Failed counts and the pending
/// list, pushes the Outbox to the server, and pulls server features (surfacing
/// conflicts). Bound to the tested <see cref="SyncCenterViewModel"/>; transport
/// and state come from DI over the shared auth-aware client.
/// </summary>
public partial class SyncPage : ContentPage
{
    private readonly IHttpClientFactory _clients;
    private readonly AppSettings _settings;
    private readonly RecordBook _book;
    private readonly Func<GeoServicesFeatureSync> _syncFactory;
    private readonly IInboxPullProgressStore _pullProgressStore;
    private readonly ILogger<SyncCenterViewModel>? _logger;
    private bool _syncRequested;

    public SyncPage(
        AppSettings settings,
        RecordBook book,
        IHttpClientFactory clients,
        Func<GeoServicesFeatureSync> syncFactory,
        IInboxPullProgressStore pullProgressStore,
        ILogger<SyncCenterViewModel>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _book = book ?? throw new ArgumentNullException(nameof(book));
        _syncFactory = syncFactory ?? throw new ArgumentNullException(nameof(syncFactory));
        _pullProgressStore = pullProgressStore ?? throw new ArgumentNullException(nameof(pullProgressStore));
        _logger = logger;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _book.InitializeAsync();
        await ForegroundSyncGate.Instance.WaitAsync();
        try
        {
            if (!_syncRequested)
            {
                BindViewModel();
            }
        }
        finally
        {
            ForegroundSyncGate.Instance.Release();
        }
    }

    private SyncCenterViewModel BindViewModel()
    {
        // 4-arg form enables bidirectional sync: the puller queries the server and
        // the active form lets the merge classify new-vs-conflicting records. The
        // persister durably saves each entry's post-upload state so a restart does
        // not re-upload (and duplicate) an already-synced record.
        var target = _settings.Target;
        var vm = new SyncCenterViewModel(
            _book.All, (entry, cancellationToken) => UploadAsync(entry, target, cancellationToken),
            PullAsync, SampleForms.FieldSite(), _book.SaveAsync,
            (entries, cancellationToken) => BatchUploadAsync(entries, target, cancellationToken),
            logger: _logger,
            canDispatchOrdinaryAdd: HasNetworkAccess,
            replayOrdinaryPhotos: ReplayOrdinaryPhotosAsync,
            canDispatchOrdinaryRecord: entry => entry.OrdinaryPhotos.All(photo => photo.Target == target));
        BindingContext = vm;
        var command = new Command(
            async () => await RunSyncAsync(),
            () => !_syncRequested && vm.SyncCommand.CanExecute(null));
        vm.SyncCommand.CanExecuteChanged += (_, _) => command.ChangeCanExecute();
        SyncButton.Command = command;
        return vm;
    }

    private static bool HasNetworkAccess() => Microsoft.Maui.Networking.Connectivity.Current.NetworkAccess
        != Microsoft.Maui.Networking.NetworkAccess.None;

    private Task<bool> ReplayOrdinaryPhotosAsync(Core.Records.FieldRecordEntry entry)
        => HasNetworkAccess()
            ? OrdinaryPhotoReplay.ReplayAsync(entry, _syncFactory(), _settings.Target, CaptureFiles.MediaDirectory, _book.SaveAsync)
            : Task.FromResult(false);

    private async Task RunSyncAsync()
    {
        if (_syncRequested)
        {
            return;
        }

        _syncRequested = true;
        (SyncButton.Command as Command)?.ChangeCanExecute();
        await ForegroundSyncGate.Instance.WaitAsync();
        try
        {
            // The record book may have changed while another screen uploaded.
            var vm = BindViewModel();
            if (vm.SyncCommand.CanExecute(null))
            {
                await vm.SyncAsync();
            }
        }
        finally
        {
            _syncRequested = false;
            (SyncButton.Command as Command)?.ChangeCanExecute();
            ForegroundSyncGate.Instance.Release();
        }
    }

    private async Task<FeatureSyncResult> UploadAsync(
        Core.Records.FieldRecordEntry entry, GeoServicesTarget target, CancellationToken cancellationToken)
    {
        if (entry.PublishedSubmission is { } submission)
        {
            if (entry.Record.Status == Honua.Sdk.Field.Records.RecordStatus.Deleted || entry.RemoteId is not null)
            {
                return FeatureSyncResult.Fail("This published form submission only supports creation.");
            }

            using var http = _clients.CreateClient(submission.Files.Count > 0
                ? MauiProgram.UploadHttpClient
                : MauiProgram.ServerHttpClient);
            return await submission.ResolveMediaPaths(CaptureFiles.MediaDirectory).UploadAsync(http, cancellationToken);
        }

        var sync = _syncFactory();

        // Route by the record's transport state and review status:
        //  - a locally deleted record -> applyEdits DELETE against its server object
        //    id (idempotent: a feature already gone is a successful no-op, never a
        //    duplicate or error). A delete of a never-synced record is a pure local
        //    drop — there is nothing on the server, so report success.
        //  - a re-edited, already-synced record (PendingUpdate carrying its RemoteId)
        //    -> applyEdits UPDATE against the existing object id (an add would
        //    duplicate the server feature).
        //  - a brand-new record -> applyEdits ADD under the durable verification hold.
        // The result flows straight to the sync center, which surfaces the server's
        // error message/code verbatim.
        if (entry.Record.Status == Honua.Sdk.Field.Records.RecordStatus.Deleted)
        {
            if (entry.RemoteId is null)
            {
                return FeatureSyncResult.Ok(null);
            }

            if (!long.TryParse(entry.RemoteId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deleteId)
                || deleteId <= 0)
            {
                return FeatureSyncResult.Fail("This feature has an invalid server object ID. Check the server layer before retrying.");
            }

            return await sync.DeleteAsync(deleteId, target, cancellationToken);
        }

        if (entry.RemoteId is { } storedObjectId)
        {
            if (!long.TryParse(storedObjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId)
                || objectId <= 0)
            {
                return FeatureSyncResult.Fail("This feature has an invalid server object ID. Check the server layer before retrying.");
            }

            return await sync.UpdateAsync(objectId, entry.Record, target, cancellationToken: cancellationToken);
        }

        return await sync.SubmitAsync(entry.Record, target, cancellationToken);
    }

    private async Task<IReadOnlyList<FeatureSyncResult>> BatchUploadAsync(
        IReadOnlyList<Core.Records.FieldRecordEntry> entries,
        GeoServicesTarget target, CancellationToken cancellationToken)
    {
        var sync = _syncFactory();
        var records = entries.Select(e => e.Record).ToList();
        return await sync.SubmitBatchAsync(records, target, cancellationToken);
    }

    private async Task<FeatureQueryResult> PullAsync(CancellationToken cancellationToken)
        => await _syncFactory()
            .QueryAsync(_settings.Target, "1=1", cancellationToken);

    private async void OnPull(object? sender, EventArgs e)
    {
        if (BindingContext is not SyncCenterViewModel vm)
        {
            return;
        }

        ResultLabel.Text = "Pulling from server…";
        var result = await vm.PullAsync();
        if (result is null)
        {
            ResultLabel.Text = "Pull failed (server unreachable?).";
            return;
        }

        ResultLabel.Text =
            $"Pulled: {result.NewRecords.Count} new · {result.Conflicts.Count} conflict(s) · {result.Unchanged.Count} unchanged.";

        if (vm.Conflicts.Count > 0)
        {
            // Hand the review screen the entry-bound conflict view-models (the whole
            // list, not just the first) plus the record store so each resolution is
            // applied to its entry AND durably persisted/re-queued — not discarded
            // (#98). vm.Conflicts is built by PullAsync, bound to the local entries.
            await Navigation.PushAsync(new ConflictReviewPage(vm.Conflicts.ToList(), _book.SaveAsync));
        }
    }

    private async void OnOpenPagedInboxPull(object? sender, EventArgs e)
    {
        const int pageSize = 100;
        var form = SampleForms.FieldSite();
        var pagedVm = new PagedInboxPullViewModel(
            _pullProgressStore,
            form,
            () => SyncCenterViewModel.IndexFeatureLayerRecords(_book.All, form.FormId),
            (offset, ct) => _syncFactory().QueryPageAsync(_settings.Target, offset, pageSize, "1=1", ct));

        await Navigation.PushAsync(new PagedInboxPullPage(pagedVm));
    }
}
