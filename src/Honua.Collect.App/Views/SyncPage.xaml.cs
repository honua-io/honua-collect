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

    protected override void OnAppearing()
    {
        base.OnAppearing();
        BindingContext = new SyncCenterViewModel(CaptureStore.All, UploadAsync);
    }

    private static async Task<string?> UploadAsync(Core.Records.CollectRecordEntry entry, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://10.0.2.2:18080") };
        http.DefaultRequestHeaders.Add("X-API-Key", "AdminPass123!");
        var result = await new GeoServicesFeatureSync(http).SubmitAsync(entry.Record, Target, cancellationToken);
        return result.Success ? result.ObjectId?.ToString() : null;
    }

    private async void OnReviewConflict(object? sender, EventArgs e)
        => await Navigation.PushAsync(new ConflictReviewPage(SampleConflict()));

    /// <summary>Builds a representative local-vs-server conflict for the review demo.</summary>
    private static RecordConflict SampleConflict()
    {
        var form = new FormDefinition
        {
            FormId = "field-site",
            Name = "Field Site",
            Sections =
            [
                new FormSection
                {
                    SectionId = "s",
                    Label = "s",
                    Fields =
                    [
                        new FormField { FieldId = "status", Label = "Status", Type = FormFieldType.Text },
                        new FormField { FieldId = "priority", Label = "Priority", Type = FormFieldType.Text },
                        new FormField { FieldId = "notes", Label = "Notes", Type = FormFieldType.Text },
                    ],
                },
            ],
        };

        var local = new FieldRecord { RecordId = "site-42", FormId = "field-site" };
        local.Values["status"] = "done";
        local.Values["priority"] = "high";
        local.Values["notes"] = "fixed on site";

        var server = new FieldRecord { RecordId = "site-42", FormId = "field-site" };
        server.Values["status"] = "in_progress";
        server.Values["priority"] = "high";          // same — not a conflict
        server.Values["notes"] = "awaiting parts";

        return RecordConflictDetector.Detect(form, local, server);
    }
}
