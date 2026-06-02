using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Sync;

namespace Honua.Collect.App.Views;

/// <summary>
/// Manual conflict-review screen (BACKLOG S1): a field-by-field diff of the local
/// and server versions with per-field and bulk keep choices, bound to the tested
/// <see cref="ConflictReviewViewModel"/>.
/// </summary>
public partial class ConflictReviewPage : ContentPage
{
    private readonly ConflictReviewViewModel _viewModel;

    public ConflictReviewPage(RecordConflict conflict)
    {
        InitializeComponent();
        _viewModel = new ConflictReviewViewModel(conflict);
        BindingContext = _viewModel;
    }

    private async void OnResolve(object? sender, EventArgs e)
    {
        var merged = _viewModel.Resolve();
        var summary = string.Join(", ", merged.Values.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        await DisplayAlert("Resolved", $"Merged record: {summary}", "OK");
        await Navigation.PopAsync();
    }
}
