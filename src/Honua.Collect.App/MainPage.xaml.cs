using Honua.Collect.App.Views;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.App;

/// <summary>
/// Home screen. Starts a new capture session over the sample form, navigates to
/// the dynamic <see cref="FormPage"/>, and on submit pushes the record to the
/// server's GeoServices Feature Server via <see cref="GeoServicesFeatureSync"/>.
/// </summary>
public partial class MainPage : ContentPage
{
    // Android emulator reaches the host loopback at 10.0.2.2. Demo credentials.
    private static readonly HttpClient Http = CreateClient();
    private static readonly GeoServicesTarget Target = new("http://10.0.2.2:18080", "mobile_offline_demo", 68910);

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
        var session = FormSession.CreateForNewRecord(SampleForms.FieldSite(), Guid.NewGuid().ToString("n"));
        var viewModel = new FormPageViewModel(session);
        viewModel.SubmitSucceeded += OnSubmitted;
        await Navigation.PushAsync(new FormPage(viewModel));
    }

    private async void OnSubmitted(object? sender, FieldRecord record)
    {
        // Stamp a capture location (the layer is point geometry) and push to the server.
        record.Location = new FieldGeoPoint(21.31, -157.81);
        StatusLabel.Text = "Submitting to server…";
        try
        {
            var result = await new GeoServicesFeatureSync(Http).SubmitAsync(record, Target);
            StatusLabel.Text = result.Success
                ? $"Synced to server — objectId {result.ObjectId}."
                : $"Sync failed: {result.Error}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Sync error: {ex.Message}";
        }
    }
}
