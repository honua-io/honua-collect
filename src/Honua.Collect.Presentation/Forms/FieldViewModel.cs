// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field;
using Honua.Collect.Core.Field.Capture;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Forms;

public sealed class FieldViewModel : ObservableObject
{
    private readonly ICaptureHost _host;
    private readonly Action _onChanged;

    public FieldViewModel(ICaptureHost host, string fieldId, Action onChanged)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        FieldId = fieldId;
        Widget = CaptureWidget.For(State.Field.Type);
    }

    private FieldState State => _host.GetField(FieldId);

    public string FieldId { get; }

    public string Label => State.Field.Label;

    public string? HelpText => State.Field.HelpText;

    public bool IsRequired => State.IsRequired;

    public CaptureWidgetKind Widget { get; }

    public string CaptureActionLabel => Widget switch
    {
        CaptureWidgetKind.Photo => "Add photo",
        CaptureWidgetKind.Video => "Record video",
        CaptureWidgetKind.Audio => "Record audio",
        CaptureWidgetKind.Signature => "Sign",
        CaptureWidgetKind.Sketch => "Sketch",
        CaptureWidgetKind.Barcode => "Scan barcode",
        CaptureWidgetKind.RecordLink => "Manage related records",
        _ => "Add attachment",
    };

    public IReadOnlyList<FieldChoice> Choices => State.AvailableChoices;

    public FieldChoice? SelectedChoice
    {
        get
        {
            var current = State.Value?.ToString();
            return Choices.FirstOrDefault(c => string.Equals(c.Value, current, StringComparison.Ordinal));
        }
        set => Value = value?.Value;
    }

    public bool IsVisible => State.IsVisible;

    public string ErrorText => string.Join(" ", State.Errors);

    public bool HasError => State.Errors.Count > 0;

    public object? Value
    {
        get => State.Value;
        set
        {
            if (!Equals(State.Value, value))
            {
                _host.SetValue(FieldId, value);
                _onChanged();
            }
        }
    }

    public int MediaCount => State.Media.Count;

    public int RelatedCount => State.Value switch
    {
        IReadOnlyCollection<FieldRecordLinkValue> links => links.Count,
        ICollection<FieldRecordLinkValue> links => links.Count,
        _ => 0,
    };

    public void CaptureMedia(string localPath, string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        _host.AddMedia(new CapturedMediaAttachment
        {
            AttachmentId = Guid.NewGuid().ToString("n"),
            FieldId = FieldId,
            LocalPath = localPath,
            ContentType = contentType,
        });
        _onChanged();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(MediaCount));
        OnPropertyChanged(nameof(RelatedCount));
    }
}
