// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Presentation.Sync;

namespace Honua.Collect.App.Views;

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
            // is lost on the next launch — the data-loss bug this screen fixes.
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
