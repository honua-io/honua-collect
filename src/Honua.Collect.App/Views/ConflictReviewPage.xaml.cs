using Honua.Collect.Presentation.Sync;

namespace Honua.Collect.App.Views;

/// <summary>
/// Manual conflict-review screen (BACKLOG S1): a field-by-field diff of the local
/// and server versions with per-field and bulk keep choices, bound to the tested
/// <see cref="ConflictReviewViewModel"/>.
/// </summary>
/// <remarks>
/// The screen walks the full list of conflicted records produced by a pull, one at
/// a time. Each review is bound to its local record entry, so pressing Resolve
/// calls <see cref="ConflictReviewViewModel.ApplyResolutionAsync"/> — applying the
/// user's chosen merge onto the entry AND durably persisting it — instead of the
/// old throwaway <c>Resolve()</c>+<c>DisplayAlert</c> path that silently discarded
/// the resolution (#98).
/// </remarks>
public partial class ConflictReviewPage : ContentPage
{
    private readonly IReadOnlyList<ConflictReviewViewModel> _reviews;
    private readonly RecordStatePersister? _persist;
    private int _index;

    public ConflictReviewPage(IReadOnlyList<ConflictReviewViewModel> reviews, RecordStatePersister? persist = null)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        InitializeComponent();
        _reviews = reviews;
        _persist = persist;
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        if (_index < _reviews.Count)
        {
            BindingContext = _reviews[_index];
        }
    }

    private async void OnResolve(object? sender, EventArgs e)
    {
        if (BindingContext is ConflictReviewViewModel vm && vm.CanApply)
        {
            // Persist + re-queue the user's chosen merge. Without this the resolution
            // is lost on the next launch — the data-loss bug this screen fixes (#98).
            await vm.ApplyResolutionAsync(_persist);
        }

        _index++;
        if (_index < _reviews.Count)
        {
            ShowCurrent();
            return;
        }

        await Navigation.PopAsync();
    }
}
