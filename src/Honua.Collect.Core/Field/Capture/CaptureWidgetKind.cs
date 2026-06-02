using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Capture;

/// <summary>
/// The kind of capture widget a form field renders as. A dynamic form renderer
/// maps each field to a widget via <see cref="For"/> and binds the matching
/// view-model.
/// </summary>
public enum CaptureWidgetKind
{
    /// <summary>Single-line or multi-line text entry.</summary>
    Text,

    /// <summary>Numeric entry.</summary>
    Number,

    /// <summary>Date, time, or date-time picker.</summary>
    DateTime,

    /// <summary>Boolean toggle.</summary>
    Toggle,

    /// <summary>Single- or multi-select choice picker.</summary>
    Choice,

    /// <summary>Camera / photo capture.</summary>
    Photo,

    /// <summary>Video capture.</summary>
    Video,

    /// <summary>Audio recording.</summary>
    Audio,

    /// <summary>Signature pad.</summary>
    Signature,

    /// <summary>Freehand sketch.</summary>
    Sketch,

    /// <summary>Generic file attachment.</summary>
    File,

    /// <summary>Barcode / QR scanner.</summary>
    Barcode,

    /// <summary>Map / GPS location picker.</summary>
    Location,

    /// <summary>Link to related record(s).</summary>
    RecordLink,

    /// <summary>Read-only calculated value.</summary>
    Calculated,
}

/// <summary>Maps <see cref="FormFieldType"/> to the widget that renders it.</summary>
public static class CaptureWidget
{
    /// <summary>Returns the widget kind for a field type.</summary>
    /// <param name="fieldType">The portable field type.</param>
    /// <returns>The widget kind to render.</returns>
    public static CaptureWidgetKind For(FormFieldType fieldType) => fieldType switch
    {
        FormFieldType.Text or FormFieldType.Address or FormFieldType.Hyperlink => CaptureWidgetKind.Text,
        FormFieldType.Numeric => CaptureWidgetKind.Number,
        FormFieldType.Date or FormFieldType.Time or FormFieldType.DateTime => CaptureWidgetKind.DateTime,
        FormFieldType.YesNo => CaptureWidgetKind.Toggle,
        FormFieldType.SingleChoice or FormFieldType.MultipleChoice or FormFieldType.Classification => CaptureWidgetKind.Choice,
        FormFieldType.Photo => CaptureWidgetKind.Photo,
        FormFieldType.Video => CaptureWidgetKind.Video,
        FormFieldType.Audio => CaptureWidgetKind.Audio,
        FormFieldType.Signature => CaptureWidgetKind.Signature,
        FormFieldType.Sketch => CaptureWidgetKind.Sketch,
        FormFieldType.File => CaptureWidgetKind.File,
        FormFieldType.Barcode => CaptureWidgetKind.Barcode,
        FormFieldType.Location => CaptureWidgetKind.Location,
        FormFieldType.RecordLink => CaptureWidgetKind.RecordLink,
        FormFieldType.Calculated => CaptureWidgetKind.Calculated,
        _ => CaptureWidgetKind.Text,
    };
}
