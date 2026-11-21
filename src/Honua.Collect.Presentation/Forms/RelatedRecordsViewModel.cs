// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Windows.Input;
using Honua.Collect.Core.Field.Related;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Forms;

/// <summary>A single linked child-record row shown on the related-records screen.</summary>
public sealed class RelatedRecordRowViewModel
{
    /// <summary>Creates a row over a linked child reference.</summary>
    /// <param name="link">The child link value.</param>
    public RelatedRecordRowViewModel(FieldRecordLinkValue link)
    {
        Link = link ?? throw new ArgumentNullException(nameof(link));
    }

    /// <summary>The underlying child link value.</summary>
    public FieldRecordLinkValue Link { get; }

    /// <summary>Identifier of the linked child record.</summary>
    public string RecordId => Link.RecordId;

    /// <summary>Display label, falling back to the record id when unlabeled.</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Link.Label) ? Link.RecordId : Link.Label;

    /// <summary>Secondary metadata line showing the target form and id.</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(Link.FormId)
        ? Link.RecordId
        : $"{Link.FormId} · {Link.RecordId}";
}

/// <summary>
/// View-model for the related-records (F4) capture screen: lists child records linked
/// from a parent record's <see cref="Honua.Sdk.Field.Forms.FormFieldType.RecordLink"/>
/// field, creates new child records against the referenced form, links existing child
/// records, and unlinks children while mirroring links through
/// <see cref="IRelatedRecordStore"/> when supplied.
/// </summary>
public sealed class RelatedRecordsViewModel : ObservableObject
{
    private readonly RelatedRecords _related;
    private readonly IRelatedRecordStore? _store;
    private readonly Func<string> _idGenerator;
    private string _newChildLabel = string.Empty;
    private string _existingRecordId = string.Empty;
    private string _existingRecordLabel = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isBusy;

    /// <summary>Creates the related-records view-model over a form session's link model.</summary>
    /// <param name="related">The parent→children relationship model.</param>
    /// <param name="store">Optional durable link store for persistence and integrity checks.</param>
    /// <param name="idGenerator">Optional generator for newly created child record ids.</param>
    public RelatedRecordsViewModel(
        RelatedRecords related,
        IRelatedRecordStore? store = null,
        Func<string>? idGenerator = null)
    {
        _related = related ?? throw new ArgumentNullException(nameof(related));
        _store = store;
        _idGenerator = idGenerator ?? (() => Guid.NewGuid().ToString("n"));
        Children = [];
        CreateChildCommand = new RelayCommand(() => _ = CreateChildAsync(), () => !IsBusy);
        LinkExistingCommand = new RelayCommand(() => _ = LinkExistingAsync(), () => !IsBusy);
        UnlinkCommand = new RelayCommand<RelatedRecordRowViewModel>(row => _ = UnlinkAsync(row), row => row is not null && !IsBusy);
        RefreshChildren();
    }

    /// <summary>Raised whenever a new child record is created and linked.</summary>
    public event EventHandler<FieldRecord>? ChildCreated;

    /// <summary>Raised whenever the linked children collection changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Screen title (the link field's label).</summary>
    public string Title => _related.Field.Label;

    /// <summary>Identifier of the parent record.</summary>
    public string ParentRecordId => _related.Parent.RecordId;

    /// <summary>Identifier of the record-link field.</summary>
    public string FieldId => _related.Field.FieldId;

    /// <summary>Target form id for child records, when constrained.</summary>
    public string? ReferencedFormId => _related.ReferencedFormId;

    /// <summary>Referential-integrity policy applied on parent delete.</summary>
    public RecordLinkBehavior Behavior => _related.Behavior;

    /// <summary>Human-readable summary of the parent-delete integrity policy.</summary>
    public string BehaviorSummary => Behavior == RecordLinkBehavior.Restrict
        ? "Delete policy: Restrict (unlink children before deleting parent)"
        : "Delete policy: Cascade";

    /// <summary>Summary header line showing count and integrity policy.</summary>
    public string Header => $"{Children.Count} linked record(s) · {BehaviorSummary}";

    /// <summary>Linked child rows in insertion order.</summary>
    public ObservableCollection<RelatedRecordRowViewModel> Children { get; }

    /// <summary>Whether any child records are currently linked.</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>Optional label entered for creating a new child record.</summary>
    public string NewChildLabel
    {
        get => _newChildLabel;
        set => SetProperty(ref _newChildLabel, value);
    }

    /// <summary>Record id entered for linking an existing child record.</summary>
    public string ExistingRecordId
    {
        get => _existingRecordId;
        set => SetProperty(ref _existingRecordId, value);
    }

    /// <summary>Optional display label entered when linking an existing child record.</summary>
    public string ExistingRecordLabel
    {
        get => _existingRecordLabel;
        set => SetProperty(ref _existingRecordLabel, value);
    }

    /// <summary>Most recent action outcome or validation message.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Whether an asynchronous link operation is in flight.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                (CreateChildCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (LinkExistingCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (UnlinkCommand as RelayCommand<RelatedRecordRowViewModel>)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Creates a new child record and links it to the parent.</summary>
    public ICommand CreateChildCommand { get; }

    /// <summary>Links an existing child record by <see cref="ExistingRecordId"/>.</summary>
    public ICommand LinkExistingCommand { get; }

    /// <summary>Unlinks the specified child row from the parent.</summary>
    public ICommand UnlinkCommand { get; }

    /// <summary>
    /// Hydrates any persisted links from <see cref="IRelatedRecordStore"/> into the
    /// live relationship model. No-op when no durable store was configured.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task LoadFromStoreAsync(CancellationToken ct = default)
    {
        if (_store is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var persisted = await _store.ListAsync(ParentRecordId, FieldId, ct).ConfigureAwait(false);
            foreach (var link in persisted)
            {
                _related.Link(link.RecordId, link.Label, link.SourceId);
            }

            RefreshChildren();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Creates a new child record against <see cref="ReferencedFormId"/>, links it
    /// to the parent, and persists the link when a store is configured.
    /// </summary>
    /// <param name="label">Optional display label; defaults to <see cref="NewChildLabel"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created child record.</returns>
    public async Task<FieldRecord> CreateChildAsync(string? label = null, CancellationToken ct = default)
    {
        var effectiveLabel = string.IsNullOrWhiteSpace(label)
            ? (string.IsNullOrWhiteSpace(NewChildLabel) ? null : NewChildLabel.Trim())
            : label.Trim();

        IsBusy = true;
        try
        {
            var childId = _idGenerator();
            var child = _related.Create(childId, effectiveLabel);

            if (_store is not null)
            {
                var link = _related.Children.Last(c => string.Equals(c.RecordId, child.RecordId, StringComparison.Ordinal));
                await _store.LinkAsync(ParentRecordId, FieldId, link, Behavior, ct).ConfigureAwait(false);
            }

            NewChildLabel = string.Empty;
            RefreshChildren();
            StatusMessage = $"Created and linked child record '{effectiveLabel ?? child.RecordId}'.";
            ChildCreated?.Invoke(this, child);
            Changed?.Invoke(this, EventArgs.Empty);
            return child;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Links an existing child record by id and persists the link when a store is configured.
    /// </summary>
    /// <param name="childRecordId">Child record id; defaults to <see cref="ExistingRecordId"/>.</param>
    /// <param name="label">Optional display label; defaults to <see cref="ExistingRecordLabel"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if a new link was added; <see langword="false"/> if invalid or duplicate.</returns>
    public async Task<bool> LinkExistingAsync(
        string? childRecordId = null,
        string? label = null,
        CancellationToken ct = default)
    {
        var id = string.IsNullOrWhiteSpace(childRecordId) ? ExistingRecordId.Trim() : childRecordId.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            StatusMessage = "Enter a child record ID to link.";
            return false;
        }

        var effectiveLabel = string.IsNullOrWhiteSpace(label)
            ? (string.IsNullOrWhiteSpace(ExistingRecordLabel) ? null : ExistingRecordLabel.Trim())
            : label.Trim();

        IsBusy = true;
        try
        {
            if (!_related.Link(id, effectiveLabel))
            {
                StatusMessage = $"Record '{id}' is already linked.";
                return false;
            }

            if (_store is not null)
            {
                var link = _related.Children.Last(c => string.Equals(c.RecordId, id, StringComparison.Ordinal));
                await _store.LinkAsync(ParentRecordId, FieldId, link, Behavior, ct).ConfigureAwait(false);
            }

            ExistingRecordId = string.Empty;
            ExistingRecordLabel = string.Empty;
            RefreshChildren();
            StatusMessage = $"Linked existing record '{effectiveLabel ?? id}'.";
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Unlinks a child row from the parent and removes it from the durable store.</summary>
    /// <param name="row">The linked child row to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if a link was removed.</returns>
    public async Task<bool> UnlinkAsync(RelatedRecordRowViewModel? row, CancellationToken ct = default)
    {
        if (row is null)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            var removed = _related.Unlink(row.RecordId);
            if (!removed)
            {
                StatusMessage = $"Record '{row.RecordId}' was not linked.";
                return false;
            }

            if (_store is not null)
            {
                await _store.UnlinkAsync(ParentRecordId, FieldId, row.RecordId, ct).ConfigureAwait(false);
            }

            RefreshChildren();
            StatusMessage = $"Unlinked '{row.DisplayLabel}'.";
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshChildren()
    {
        Children.Clear();
        foreach (var link in _related.Children)
        {
            Children.Add(new RelatedRecordRowViewModel(link));
        }

        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(HasChildren));
    }
}
