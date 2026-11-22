// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Presentation.Sync;

namespace Honua.Collect.App.Views;

public partial class PagedInboxPullPage : ContentPage
{
    private readonly PagedInboxPullViewModel _viewModel;

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
