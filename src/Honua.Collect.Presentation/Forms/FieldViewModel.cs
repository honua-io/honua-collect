using Honua.Collect.Core.Field;
using Honua.Collect.Core.Field.Capture;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Presentation.Forms;

/// <summary>
/// View-model for one field on a capture screen. Binds a control to the live
/// <see cref="FieldState"/> in the runtime: editing <see cref="Value"/> pushes
/// through to the <see cref="ICaptureHost"/> (the form session or a repeat row),
/// which recomputes visibility, calculated fields, and validation; the parent
/// then calls <see cref="Refresh"/> so every field reflects the new state.
/// </summary>
public sealed class FieldViewModel : ObservableObject
{
    private readonly ICaptureHost _host;
    private readonly Action _onChanged;

    /// <summary>Creates a field view-model bound to a host field.</summary>
    /// <param name="host">The form session or repeat row that owns the field.</param>
    /// <param name="fieldId">Field identifier.</param>
    /// <param name="onChanged">Callback the setter invokes so the parent can refresh siblings.</param>
    public FieldViewModel(ICaptureHost host, string fieldId, Action onChanged)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        FieldId = fieldId;
        Widget = CaptureWidget.For(State.Field.Type);
    }

    private FieldState State => _host.GetField(FieldId);

    /// <summary>Stable field identifier.</summary>
    public string FieldId { get; }

    /// <summary>Field label.</summary>
    public string Label => State.Field.Label;

    /// <summary>Optional help text.</summary>
    public string? HelpText => State.Field.HelpText;

    /// <summary>Whether a value is required.</summary>
    public bool IsRequired => State.IsRequired;

    /// <summary>The widget kind a renderer should present.</summary>
    public CaptureWidgetKind Widget { get; }

    /// <summary>Allowed choices for choice/classification fields.</summary>
    public IReadOnlyList<FieldChoice> Choices => State.Field.Choices;

    /// <summary>
    /// The selected choice for single-choice pickers. Maps between the stored
    /// value (a choice <see cref="FieldChoice.Value"/> string) and the
    /// <see cref="FieldChoice"/> a <c>Picker.SelectedItem</c> binds to, so the
    /// field stores the value — not the choice object.
    /// </summary>
    public FieldChoice? SelectedChoice
    {
        get
        {
            var current = State.Value?.ToString();
            return Choices.FirstOrDefault(c => string.Equals(c.Value, current, StringComparison.Ordinal));
        }
        set => Value = value?.Value;
    }

    /// <summary>Whether the field is currently shown.</summary>
    public bool IsVisible => State.IsVisible;

    /// <summary>Validation messages joined for display.</summary>
    public string ErrorText => string.Join(" ", State.Errors);

    /// <summary>Whether the field currently has a validation error.</summary>
    public bool HasError => State.Errors.Count > 0;

    /// <summary>The current value. Setting it pushes through the runtime.</summary>
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

    /// <summary>Number of media attachments captured for this field.</summary>
    public int MediaCount => State.Media.Count;

    /// <summary>
    /// Registers a captured media file against this field (used by the photo/
    /// signature/sketch widgets). Pushes through the host and refreshes.
    /// </summary>
    /// <param name="localPath">Host-local path to the captured file.</param>
    /// <param name="contentType">Media content type, when known.</param>
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

    /// <summary>Re-reads runtime state and raises change notification for all bound members.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(MediaCount));
    }
}
