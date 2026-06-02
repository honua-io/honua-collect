using Honua.Collect.App.Views;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Presentation.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.App;

/// <summary>
/// Home screen. Starts a new capture session over the sample form and navigates
/// to the dynamic <see cref="FormPage"/>. All capture behaviour lives in the
/// tested <see cref="FormPageViewModel"/>.
/// </summary>
public partial class MainPage : ContentPage
{
    private int _sequence;

    public MainPage()
    {
        InitializeComponent();
    }

    private async void OnNewInspectionClicked(object? sender, EventArgs e)
    {
        var recordId = $"insp-{++_sequence}";
        var session = FormSession.CreateForNewRecord(SampleForms.AssetInspection(), recordId);
        var viewModel = new FormPageViewModel(session);

        viewModel.SubmitSucceeded += OnSubmitted;
        viewModel.DraftSaved += OnDraftSaved;

        await Navigation.PushAsync(new FormPage(viewModel));
    }

    private void OnSubmitted(object? sender, FieldRecord record)
        => StatusLabel.Text = $"Submitted {record.RecordId} ({record.Status}).";

    private void OnDraftSaved(object? sender, FieldRecord record)
        => StatusLabel.Text = $"Draft saved: {record.RecordId}.";
}
