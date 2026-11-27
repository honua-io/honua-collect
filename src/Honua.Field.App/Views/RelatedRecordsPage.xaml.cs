// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Presentation.Forms;

namespace Honua.Field.App.Views;

/// <summary>
/// Related-records (F4) capture screen: lists child records linked from a parent
/// record's record-link field and lets the worker create new child records, link
/// existing ones, or unlink them.
/// </summary>
public partial class RelatedRecordsPage : ContentPage
{
    private readonly RelatedRecordsViewModel _viewModel;

    /// <summary>Creates the related-records screen over its tested view-model.</summary>
    /// <param name="viewModel">View-model bound to the parent record's link field.</param>
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
