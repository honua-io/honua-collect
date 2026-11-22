// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Presentation.History;

namespace Honua.Collect.App.Views;

public partial class RecordHistoryPage : ContentPage
{
    private readonly RecordHistoryViewModel _viewModel;

    public RecordHistoryPage(RecordHistoryViewModel viewModel)
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
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Edit history", $"Could not load edit history: {ex.Message}", "OK");
        }
    }

    private async void OnRevertVersionClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: RecordEditVersionViewModel version })
        {
            return;
        }

        try
        {
            await _viewModel.RevertToVersionAsync(version);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Edit history", $"Revert failed: {ex.Message}", "OK");
        }
    }
}
