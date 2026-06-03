using Honua.Collect.App.Services;
using Honua.Collect.Core.Storage;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Sync;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly HttpClient _http =
        ServiceHelper.Get<IHttpClientFactory>().CreateClient(MauiProgram.ServerHttpClient);

    public SyncPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _book.InitializeAsync();
        // 4-arg form enables bidirectional sync: the puller queries the server and
        // the active form lets the merge classify new-vs-conflicting records.
        BindingContext = new SyncCenterViewModel(_book.All, UploadAsync, PullAsync, SampleForms.FieldSite());
    }

    private async Task<string?> UploadAsync(Core.Records.CollectRecordEntry entry, CancellationToken cancellationToken)
    {
        var result = await new GeoServicesFeatureSync(_http).SubmitAsync(entry.Record, _settings.Target, cancellationToken);
        return result.Success ? result.ObjectId?.ToString() : null;
    }

    private async Task<FeatureQueryResult> PullAsync(CancellationToken cancellationToken)
        => await new GeoServicesFeatureSync(_http).QueryAsync(_settings.Target, "1=1", cancellationToken);

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

        if (result.Conflicts.Count > 0)
        {
            await Navigation.PushAsync(new ConflictReviewPage(result.Conflicts[0]));
        }
    }
}
