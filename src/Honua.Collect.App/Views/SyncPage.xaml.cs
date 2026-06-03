using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Sync;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.App.Views;

/// <summary>
/// The offline sync center: shows the Outbox/Sent/Failed counts and the pending
/// list, and pushes the Outbox to the server's Feature Server on demand. Bound
/// to the tested <see cref="SyncCenterViewModel"/>.
/// </summary>
public partial class SyncPage : ContentPage
{
    private static readonly GeoServicesTarget Target = new("http://10.0.2.2:18080", "mobile_offline_demo", 68910);

    public SyncPage()
    {
        InitializeComponent();
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri("http://10.0.2.2:18080") };
        http.DefaultRequestHeaders.Add("X-API-Key", "AdminPass123!");
        return http;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // 4-arg form enables bidirectional sync: the puller queries the server and
        // the active form lets the merge classify new-vs-conflicting records.
        BindingContext = new SyncCenterViewModel(CaptureStore.All, UploadAsync, PullAsync, SampleForms.FieldSite());
    }

    private static async Task<string?> UploadAsync(Core.Records.CollectRecordEntry entry, CancellationToken cancellationToken)
    {
        using var http = CreateClient();
        var result = await new GeoServicesFeatureSync(http).SubmitAsync(entry.Record, Target, cancellationToken);
        return result.Success ? result.ObjectId?.ToString() : null;
    }

    private static async Task<FeatureQueryResult> PullAsync(CancellationToken cancellationToken)
    {
        using var http = CreateClient();
        return await new GeoServicesFeatureSync(http).QueryAsync(Target, "1=1", cancellationToken);
    }

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
