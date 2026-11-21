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
    private readonly IDeviceAuthorization _authorization;

    public RecordsPage(
        RecordBook book,
        IRecordHistoryStore historyStore,
        RecordRevertService revertService,
        IAuthSessionStore sessionStore,
        IDeviceAuthorization authorization)
    {
        _book = book ?? throw new ArgumentNullException(nameof(book));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _revertService = revertService ?? throw new ArgumentNullException(nameof(revertService));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
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
        var canEdit = !string.IsNullOrWhiteSpace(editorId)
            && _authorization.Can(CollectPermission.EditRecords, row.RecordId);

        var historyVm = canEdit
            ? new RecordHistoryViewModel(_historyStore, row.Entry, _revertService, editorId!)
            : new RecordHistoryViewModel(_historyStore, row.RecordId);

        await Navigation.PushAsync(new RecordHistoryPage(historyVm));
    }
}
