using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Forms;

/// <summary>
/// Live capture state for a single <see cref="FormField"/> within a
/// <see cref="FormSession"/>. This is the product-owned view-state that capture
/// widgets (text inputs, choice pickers, camera/signature/barcode widgets) bind
/// to: the current value, whether the field is currently visible and required,
/// any validation messages, and captured media.
/// </summary>
/// <remarks>
/// The SDK's <see cref="FormField"/> is an immutable <em>definition</em>; this
/// type carries the mutable per-capture state the SDK contract deliberately
/// keeps out of the form schema. Only the owning <see cref="FormSession"/>
/// mutates it, so setters are internal.
/// </remarks>
public sealed class FieldState
{
    private readonly List<CapturedMediaAttachment> _media = [];
    private readonly List<string> _errors = [];

    internal FieldState(FormField field, FormSection section, int repeatInstance)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        Section = section ?? throw new ArgumentNullException(nameof(section));
        RepeatInstance = repeatInstance;
        AvailableChoices = field.Choices ?? [];
    }

    /// <summary>The SDK field definition this state captures a value for.</summary>
    public FormField Field { get; }

    /// <summary>The section the field belongs to.</summary>
    public FormSection Section { get; }

    /// <summary>
    /// Zero-based repeat instance index. Always 0 for fields in non-repeatable
    /// sections; identifies which repeat row this state belongs to otherwise.
    /// </summary>
    public int RepeatInstance { get; }

    /// <summary>Stable field identifier (mirrors <see cref="FormField.FieldId"/>).</summary>
    public string FieldId => Field.FieldId;

    /// <summary>Current captured value, or <see langword="null"/> when unset.</summary>
    public object? Value { get; internal set; }

    /// <summary>
    /// Whether the field is currently shown, after evaluating its visibility
    /// rule and the visibility of the field it depends on. Hidden fields are
    /// excluded from validation.
    /// </summary>
    public bool IsVisible { get; internal set; } = true;

    /// <summary>
    /// Whether a value is currently required. Mirrors <see cref="FormField.Required"/>
    /// but is only enforced while <see cref="IsVisible"/> is <see langword="true"/>.
    /// </summary>
    public bool IsRequired => Field.Required;

    /// <summary>Validation messages for this field from the last validation pass.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Whether the field had no validation errors at the last pass.</summary>
    public bool IsValid => _errors.Count == 0;

    /// <summary>
    /// The choice options currently available for this field. For a plain choice
    /// field this is the field's full <see cref="FormField.Choices"/>; for a
    /// cascading/dependent select (BACKLOG F3) it is the subset whose
    /// <see cref="Sdk.Field.Forms.FieldChoice.ParentValue"/> matches the parent
    /// field's current value. Renderers bind their picker to this rather than the
    /// raw field choices so cascades take effect.
    /// </summary>
    public IReadOnlyList<FieldChoice> AvailableChoices { get; internal set; }

    /// <summary>Media captured against this field (kept with host-local paths).</summary>
    public IReadOnlyList<CapturedMediaAttachment> Media => _media;

    internal void AddMedia(CapturedMediaAttachment attachment) => _media.Add(attachment);

    internal bool RemoveMedia(string attachmentId)
        => _media.RemoveAll(m => string.Equals(m.AttachmentId, attachmentId, StringComparison.Ordinal)) > 0;

    internal void SetErrors(IEnumerable<string> messages)
    {
        _errors.Clear();
        _errors.AddRange(messages);
    }
}
