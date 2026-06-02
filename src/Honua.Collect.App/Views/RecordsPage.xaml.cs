using Honua.Collect.Presentation.Records;

namespace Honua.Collect.App.Views;

/// <summary>The Drafts/Outbox/Sent records screen, rebuilt from the capture store on view.</summary>
public partial class RecordsPage : ContentPage
{
    public RecordsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        BindingContext = new RecordBoxViewModel(CaptureStore.All);
    }
}
