// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Windows.Input;
using Honua.Collect.Core.Field.Related;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Forms;

public sealed class RelatedRecordRowViewModel
{
    public RelatedRecordRowViewModel(FieldRecordLinkValue link)
    {
        Link = link ?? throw new ArgumentNullException(nameof(link));
    }

    public FieldRecordLinkValue Link { get; }

    public string RecordId => Link.RecordId;

    public string DisplayLabel => string.IsNullOrWhiteSpace(Link.Label) ? Link.RecordId : Link.Label;

    public string Subtitle => string.IsNullOrWhiteSpace(Link.FormId)
        ? Link.RecordId
        : $"{Link.FormId} · {Link.RecordId}";
}

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

    public RelatedRecordsViewModel(
        RelatedRecords related,
        IRelatedRecordStore? store = null,
        Func<string>? idGenerator = null)
    {
        _related = related ?? throw new ArgumentNullException(nameof(related));
        _store = store;
        _idGenerator = idGenerator ?? (() => Guid.NewGuid().ToString("n"));
        Children = [];
        CreateChildCommand = new RelayCommand(() => _ = RunCommandAsync(() => CreateChildAsync()), () => !IsBusy);
        LinkExistingCommand = new RelayCommand(() => _ = RunCommandAsync(() => LinkExistingAsync()), () => !IsBusy);
        UnlinkCommand = new RelayCommand<RelatedRecordRowViewModel>(
            row => _ = RunCommandAsync(() => UnlinkAsync(row)),
            row => row is not null && !IsBusy);
        RefreshChildren();
    }

    public event EventHandler<FieldRecord>? ChildCreated;

    public event EventHandler? Changed;

    public string Title => _related.Field.Label;

    public string ParentRecordId => _related.Parent.RecordId;

    public string FieldId => _related.Field.FieldId;

    public string? ReferencedFormId => _related.ReferencedFormId;

    public RecordLinkBehavior Behavior => _related.Behavior;

    public string BehaviorSummary => Behavior == RecordLinkBehavior.Restrict
        ? "Delete policy: Restrict (unlink children before deleting parent)"
        : "Delete policy: Cascade";

    public string Header => $"{Children.Count} linked record(s) · {BehaviorSummary}";

    public ObservableCollection<RelatedRecordRowViewModel> Children { get; }

    public bool HasChildren => Children.Count > 0;

    public string NewChildLabel
    {
        get => _newChildLabel;
        set => SetProperty(ref _newChildLabel, value);
    }

    public string ExistingRecordId
    {
        get => _existingRecordId;
        set => SetProperty(ref _existingRecordId, value);
    }

    public string ExistingRecordLabel
    {
        get => _existingRecordLabel;
        set => SetProperty(ref _existingRecordLabel, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

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

    public ICommand CreateChildCommand { get; }

    public ICommand LinkExistingCommand { get; }

    public ICommand UnlinkCommand { get; }

    public async Task LoadFromStoreAsync(CancellationToken ct = default)
    {
        if (_store is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var persisted = await _store.ListAsync(ParentRecordId, FieldId, ct);
            var added = false;
            foreach (var link in persisted)
            {
                if (_related.Link(link.RecordId, link.Label, link.SourceId))
                {
                    added = true;
                }
            }

            RefreshChildren();
            if (added)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<FieldRecord?> CreateChildAsync(string? label = null, CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return null;
        }

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
                try
                {
                    await _store.LinkAsync(ParentRecordId, FieldId, link, Behavior, ct);
                }
                catch
                {
                    _related.Unlink(child.RecordId);
                    throw;
                }
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

    public async Task<bool> LinkExistingAsync(
        string? childRecordId = null,
        string? label = null,
        CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return false;
        }

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
                try
                {
                    await _store.LinkAsync(ParentRecordId, FieldId, link, Behavior, ct);
                }
                catch
                {
                    _related.Unlink(id);
                    throw;
                }
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

    public async Task<bool> UnlinkAsync(RelatedRecordRowViewModel? row, CancellationToken ct = default)
    {
        if (row is null || IsBusy)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            if (!_related.Children.Any(c => string.Equals(c.RecordId, row.RecordId, StringComparison.Ordinal)))
            {
                StatusMessage = $"Record '{row.RecordId}' was not linked.";
                return false;
            }

            if (_store is not null)
            {
                await _store.UnlinkAsync(ParentRecordId, FieldId, row.RecordId, ct);
            }

            _related.Unlink(row.RecordId);
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

    private async Task RunCommandAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Operation failed: {ex.Message}";
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
