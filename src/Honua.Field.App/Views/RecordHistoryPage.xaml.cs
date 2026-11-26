// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Presentation.History;

namespace Honua.Field.App.Views;

/// <summary>
/// Edit-history and undo/revert screen (BACKLOG #38 / #101). Displays durable edit
/// versions newest-first and triggers undo-last-change or revert-to-version via
/// <see cref="RecordHistoryViewModel"/>.
/// </summary>
public partial class RecordHistoryPage : ContentPage
{
    private readonly RecordHistoryViewModel _viewModel;

    /// <summary>Creates the edit-history page over its tested view-model.</summary>
    /// <param name="viewModel">History and revert view-model for the selected record.</param>
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
