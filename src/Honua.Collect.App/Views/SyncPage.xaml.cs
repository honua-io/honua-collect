using System.Globalization;
using Honua.Collect.App.Services;
using Honua.Collect.Core.Storage;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Sync;

namespace Honua.Collect.App.Views;

/// <summary>
/// The offline sync center: shows the Outbox/Sent/Failed counts and the pending
/// list, pushes the Outbox to the server, and pulls server features (surfacing
/// conflicts). Bound to the tested <see cref="SyncCenterViewModel"/>; transport
/// and state come from DI over the shared auth-aware client.
/// </summary>
public partial class SyncPage : ContentPage
{
    private readonly AppSettings _settings = ServiceHelper.Get<AppSettings>();
    private readonly RecordBook _book = ServiceHelper.Get<RecordBook>();

    public SyncPage()
    {
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
            _book.All, UploadAsync, PullAsync, SampleForms.FieldSite(), _book.SaveAsync, BatchUploadAsync);
    }

    private async Task<FeatureSyncResult> UploadAsync(Core.Records.CollectRecordEntry entry, CancellationToken cancellationToken)
    {
        var sync = ServiceHelper.Get<GeoServicesFeatureSync>();

        // Route by the record's transport state and review status:
        //  - a locally deleted record -> applyEdits DELETE against its server object
        //    id (idempotent: a feature already gone is a successful no-op, never a
        //    duplicate or error). A delete of a never-synced record is a pure local
        //    drop — there is nothing on the server, so report success.
        //  - a re-edited, already-synced record (PendingUpdate carrying its RemoteId)
        //    -> applyEdits UPDATE against the existing object id (an add would
        //    duplicate the server feature).
        //  - a brand-new record -> applyEdits ADD (carrying its stable client
        //    GlobalID so a retried/lost-response add cannot duplicate).
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

        if (entry.IsServerUpdate
            && long.TryParse(entry.RemoteId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
        {
            return await sync.UpdateAsync(objectId, entry.Record, _settings.Target, cancellationToken: cancellationToken);
        }

        return await sync.SubmitAsync(entry.Record, _settings.Target, cancellationToken);
    }

    private async Task<IReadOnlyList<FeatureSyncResult>> BatchUploadAsync(
        IReadOnlyList<Core.Records.CollectRecordEntry> entries,
        CancellationToken cancellationToken)
    {
        var sync = ServiceHelper.Get<GeoServicesFeatureSync>();
        var records = entries.Select(e => e.Record).ToList();
        return await sync.SubmitBatchAsync(records, _settings.Target, cancellationToken);
    }

    private async Task<FeatureQueryResult> PullAsync(CancellationToken cancellationToken)
        => await ServiceHelper.Get<GeoServicesFeatureSync>()
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
}
