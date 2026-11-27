// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.App.Services;
using Honua.Field.Core.Field.Forms;
using Honua.Field.Core.Field.Related;
using Honua.Field.Presentation.Assignments;
using Honua.Field.Presentation.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Field.App.Views;

/// <summary>
/// The assignment / task inbox (BACKLOG E5). Lists dispatched
/// <see cref="Honua.Field.Core.Assignments.FieldAssignment"/>s; tapping one
/// starts a capture <see cref="FormSession"/> over the assignment's form
/// (the bundled <see cref="SampleForms"/> for the demo), prefilled with the
/// assignment context, and pushes the dynamic <see cref="FormPage"/>. On submit
/// the assignment transitions to in-progress, linked to the new record.
/// </summary>
public partial class InboxPage : ContentPage
{
    // The demo inbox belongs to this worker; assignments capture against the
    // bundled field-site form so the screen works fully offline on first run.
    private const string DemoUserId = "mobile_offline_demo";

    private readonly Core.Storage.RecordBook _book;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRelatedRecordStore _relatedStore;
    private readonly AppSettings _settings;
    private InboxViewModel? _viewModel;

    public InboxPage(
        Core.Storage.RecordBook book,
        IHttpClientFactory httpClientFactory,
        IRelatedRecordStore relatedStore,
        AppSettings settings)
    {
        _book = book ?? throw new ArgumentNullException(nameof(book));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _relatedStore = relatedStore ?? throw new ArgumentNullException(nameof(relatedStore));
        _settings = settings;
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Rebuild on each appearance so completed assignments fall off the list.
        if (_viewModel is not null)
        {
            _viewModel.OpenRequested -= OnOpenRequested;
        }

        _viewModel = InboxViewModel.CreateDemo(DemoUserId, SampleForms.FieldSite().FormId);
        _viewModel.OpenRequested += OnOpenRequested;
        BindingContext = _viewModel;
    }

    private void OnAssignmentSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is AssignmentRowViewModel row)
        {
            _viewModel?.OpenAssignment(row);
        }

        if (sender is CollectionView view)
        {
            view.SelectedItem = null; // allow re-tapping the same row
        }
    }

    private async void OnOpenRequested(object? sender, Honua.Field.Core.Assignments.FieldAssignment assignment)
    {
        var recordId = Guid.NewGuid().ToString("n");

        // Seed the capture with the assignment context so the worker isn't
        // re-typing what dispatch already knows.
        var seed = new FieldRecord { RecordId = recordId, FormId = assignment.FormId };
        seed.Values["site_name"] = assignment.Title;

        var session = SampleForms.CreateFieldSiteSession(recordId, seed);

        // Mark the assignment in-progress, linked to the record being captured.
        _viewModel?.Start(assignment, recordId);

        var viewModel = new FormPageViewModel(session);
        var page = new FormPage(viewModel, _httpClientFactory, _relatedStore, _book);
        page.OnOrdinarySubmission(_settings.Target, assignment.Location);
        await Navigation.PushAsync(page);
    }
}
