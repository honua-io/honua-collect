// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
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
    private readonly PublishedOutboxReplay _publishedReplay;
    private readonly AppSettings _settings;
    private readonly RecordBook _book;
    private readonly Func<GeoServicesFeatureSync> _syncFactory;
    private readonly IInboxPullProgressStore _pullProgressStore;
    private readonly ILogger<SyncCenterViewModel>? _logger;

    public SyncPage(
        AppSettings settings,
        RecordBook book,
        PublishedOutboxReplay publishedReplay,
        Func<GeoServicesFeatureSync> syncFactory,
        IInboxPullProgressStore pullProgressStore,
        ILogger<SyncCenterViewModel>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _publishedReplay = publishedReplay;
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
        // 4-arg form enables bidirectional sync: the puller queries the server and
        // the active form lets the merge classify new-vs-conflicting records. The
        // persister durably saves each entry's post-upload state so a restart does
        // not re-upload (and duplicate) an already-synced record.
        BindingContext = new SyncCenterViewModel(
            _book.All, UploadAsync, PullAsync, SampleForms.FieldSite(), _book.SaveAsync, BatchUploadAsync,
            logger: _logger, replayPublished: (entry, ct) => _publishedReplay.ReplayAsync(entry, ct),
            canDispatchOrdinaryAdd: () => Microsoft.Maui.Networking.Connectivity.Current.NetworkAccess
                != Microsoft.Maui.Networking.NetworkAccess.None);
    }

    private async Task<FeatureSyncResult> UploadAsync(Core.Records.FieldRecordEntry entry, CancellationToken cancellationToken)
    {
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
            if (entry.RemoteId is { } remoteId
                && long.TryParse(remoteId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deleteId))
            {
                return await sync.DeleteAsync(deleteId, _settings.Target, cancellationToken);
            }

            return FeatureSyncResult.Ok(null);
        }

        if (long.TryParse(entry.RemoteId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
        {
            return await sync.UpdateAsync(objectId, entry.Record, _settings.Target, cancellationToken: cancellationToken);
        }

        return await sync.SubmitAsync(entry.Record, _settings.Target, cancellationToken);
    }

    private async Task<IReadOnlyList<FeatureSyncResult>> BatchUploadAsync(
        IReadOnlyList<Core.Records.FieldRecordEntry> entries,
        CancellationToken cancellationToken)
    {
        var sync = _syncFactory();
        var records = entries.Select(e => e.Record).ToList();
        return await sync.SubmitBatchAsync(records, _settings.Target, cancellationToken);
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

        if (vm.HasUnsavedPullConflicts)
        {
            ResultLabel.Text = "Some conflicts could not be saved on this device. Pull again before restarting to keep the review available offline.";
            return;
        }

        if (result.Conflicts.Count > 0)
        {
            await OpenConflictReviewAsync();
        }
    }

    private async void OnReviewConflicts(object? sender, EventArgs e) => await OpenConflictReviewAsync();

    private async Task OpenConflictReviewAsync()
    {
        if (BindingContext is SyncCenterViewModel { HasReviewableConflicts: true } vm)
        {
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
