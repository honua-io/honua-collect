using Honua.Collect.App.Services;
using Honua.Collect.Core.Storage;
using Honua.Collect.Presentation.Records;

namespace Honua.Collect.App.Views;

/// <summary>The Drafts/Outbox/Sent records screen, rebuilt from the record book on view.</summary>
public partial class RecordsPage : ContentPage
{
    private readonly RecordBook _book = ServiceHelper.Get<RecordBook>();

    public RecordsPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _book.InitializeAsync();
        BindingContext = new RecordBoxViewModel(_book.All);
    }
}
