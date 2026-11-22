// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Presentation.Forms;

namespace Honua.Collect.App.Views;

public partial class RelatedRecordsPage : ContentPage
{
    private readonly RelatedRecordsViewModel _viewModel;

    public RelatedRecordsPage(RelatedRecordsViewModel viewModel)
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
            await _viewModel.LoadFromStoreAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Related records", $"Could not load linked records: {ex.Message}", "OK");
        }
    }

    private async void OnUnlinkClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: RelatedRecordRowViewModel row })
        {
            return;
        }

        try
        {
            await _viewModel.UnlinkAsync(row);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Related records", $"Could not unlink record: {ex.Message}", "OK");
        }
    }
}
