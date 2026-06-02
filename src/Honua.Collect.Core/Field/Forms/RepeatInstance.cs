using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Forms;

/// <summary>
/// One filled-in row of a repeatable section (a Survey123 "repeat" / Fulcrum
/// "repeatable section" instance). Each instance is a self-contained capture
/// scope: it reuses the full <see cref="FormSession"/> runtime — visibility,
/// calculated fields, validation, media — over a synthesized single-section
/// form, so a repeat row behaves exactly like a mini-form.
/// </summary>
public sealed class RepeatInstance
{
    private readonly FormSession _session;

    internal RepeatInstance(string instanceId, FormSession session)
    {
        InstanceId = instanceId;
        _session = session;
    }

    /// <summary>Stable identifier for this row within its group.</summary>
    public string InstanceId { get; }

    /// <summary>Field states for the row, in section order.</summary>
    public IReadOnlyList<FieldState> Fields => _session.Fields;

    /// <summary>Field states currently visible in the row.</summary>
    public IEnumerable<FieldState> VisibleFields => _session.VisibleFields;

    /// <summary>Gets the live state for a field in the row.</summary>
    /// <param name="fieldId">Field identifier.</param>
    /// <returns>The field state.</returns>
    public FieldState GetField(string fieldId) => _session.GetField(fieldId);

    /// <summary>Gets the current value of a field in the row.</summary>
    /// <param name="fieldId">Field identifier.</param>
    /// <returns>The current value, or <see langword="null"/>.</returns>
    public object? GetValue(string fieldId) => _session.GetValue(fieldId);

    /// <summary>Sets a field value in the row and recomputes the row.</summary>
    /// <param name="fieldId">Field identifier.</param>
    /// <param name="value">New value.</param>
    public void SetValue(string fieldId, object? value) => _session.SetValue(fieldId, value);

    /// <summary>Adds a captured media attachment to a field in the row.</summary>
    /// <param name="attachment">Captured media.</param>
    public void AddMedia(CapturedMediaAttachment attachment) => _session.AddMedia(attachment);

    /// <summary>Validates the row in isolation.</summary>
    /// <returns>The row's validation result.</returns>
    public FormValidationResult Validate() => _session.Validate();

    /// <summary>A snapshot of the row's captured values, for persistence into the parent record.</summary>
    /// <returns>A copy of the row's value map.</returns>
    internal Dictionary<string, object?> SnapshotValues()
        => new(_session.Record.Values, StringComparer.OrdinalIgnoreCase);
}
