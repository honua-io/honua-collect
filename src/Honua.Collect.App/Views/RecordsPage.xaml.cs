// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.App.Services;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Core.History;
using Honua.Collect.Core.Storage;
using Honua.Collect.Presentation.History;
using Honua.Collect.Presentation.Records;

namespace Honua.Collect.App.Views;

/// <summary>The Drafts/Outbox/Sent records screen, rebuilt from the record book on view.</summary>
public partial class RecordsPage : ContentPage
{
    private readonly RecordBook _book;
    private readonly IRecordHistoryStore _historyStore;
    private readonly RecordRevertService _revertService;
    private readonly IAuthSessionStore _sessionStore;

    public RecordsPage(
        RecordBook book,
        IRecordHistoryStore historyStore,
        RecordRevertService revertService,
        IAuthSessionStore sessionStore)
    {
        _book = book ?? throw new ArgumentNullException(nameof(book));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _revertService = revertService ?? throw new ArgumentNullException(nameof(revertService));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _book.InitializeAsync();
        BindingContext = new RecordBoxViewModel(_book.All);
    }

    private async void OnRecordSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is CollectionView view)
        {
            view.SelectedItem = null;
        }

        if (e.CurrentSelection.FirstOrDefault() is not RecordRowViewModel row)
        {
            return;
        }

        var editorId = _sessionStore.Current?.UserId;
        if (string.IsNullOrWhiteSpace(editorId))
        {
            editorId = "local_field_user";
        }

        var historyVm = new RecordHistoryViewModel(_historyStore, row.Entry, _revertService, editorId);
        await Navigation.PushAsync(new RecordHistoryPage(historyVm));
    }
}
