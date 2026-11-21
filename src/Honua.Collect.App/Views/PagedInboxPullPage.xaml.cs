// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Presentation.Sync;

namespace Honua.Collect.App.Views;

/// <summary>
/// Resilient paged inbox pull screen (BACKLOG #38 / #101). Surfaces durable cursor
/// progress and lets the worker resume an interrupted pull or restart from offset 0.
/// </summary>
public partial class PagedInboxPullPage : ContentPage
{
    private readonly PagedInboxPullViewModel _viewModel;

    /// <summary>Creates the paged inbox pull page over its tested view-model.</summary>
    /// <param name="viewModel">Resilient paged inbox pull view-model.</param>
    public PagedInboxPullPage(PagedInboxPullViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _viewModel.LoadCursorAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Paged inbox pull", $"Could not load pull cursor: {ex.Message}", "OK");
        }
    }
}
