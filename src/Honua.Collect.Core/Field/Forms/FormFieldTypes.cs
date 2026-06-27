using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Forms;

/// <summary>
/// Single source of truth for how <see cref="FormFieldType"/> values are classified
/// across the app. Export column emission, form-session media handling, default-value
/// seeding, report rendering, and AI field extraction all share these predicates, so a
/// newly added field type only has to be slotted in here once rather than in every
/// copy of the same clause.
/// </summary>
public static class FormFieldTypes
{
    /// <summary>
    /// Whether the field captures binary media (a photo, video, audio clip, signature,
    /// sketch, or attached file) rather than a scalar value. Media fields are excluded
    /// from tabular export columns, are seeded/derived differently in a form session,
    /// are rendered as attachments in reports, and are never targets for text/photo AI
    /// extraction.
    /// </summary>
    /// <param name="type">The field type to classify.</param>
    /// <returns><see langword="true"/> when the type holds binary media.</returns>
    public static bool IsMedia(FormFieldType type)
        => type is FormFieldType.Photo or FormFieldType.Video or FormFieldType.Audio
            or FormFieldType.Signature or FormFieldType.Sketch or FormFieldType.File;
}
