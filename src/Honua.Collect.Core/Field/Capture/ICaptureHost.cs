namespace Honua.Collect.Core.Field.Forms;

/// <summary>
/// The minimal capture surface a widget needs from whatever owns the field — a
/// top-level <see cref="FormSession"/> or a repeat row (<see cref="RepeatInstance"/>).
/// Capture widgets bind to this so the same photo/barcode/etc. widget works in
/// the main form and inside a repeat row without knowing which it is.
/// </summary>
public interface ICaptureHost
{
    /// <summary>Gets the live state for a field.</summary>
    /// <param name="fieldId">Field identifier.</param>
    /// <returns>The field state.</returns>
    FieldState GetField(string fieldId);

    /// <summary>Sets a field value.</summary>
    /// <param name="fieldId">Field identifier.</param>
    /// <param name="value">New value.</param>
    void SetValue(string fieldId, object? value);

    /// <summary>Adds a captured media attachment to a field.</summary>
    /// <param name="attachment">Captured media (must specify its field).</param>
    void AddMedia(CapturedMediaAttachment attachment);

    /// <summary>Removes a captured media attachment from a field.</summary>
    /// <param name="fieldId">Field the attachment is bound to.</param>
    /// <param name="attachmentId">Attachment identifier.</param>
    /// <returns><see langword="true"/> if an attachment was removed.</returns>
    bool RemoveMedia(string fieldId, string attachmentId);
}
