using Honua.Collect.Core.Field.Capture;

namespace Honua.Collect.Presentation.Forms;

/// <summary>
/// The editor template a dynamic-form renderer should pick for a field, abstracted
/// away from any concrete MAUI <c>DataTemplate</c>. The app's template selector maps
/// this to the matching <c>DataTemplate</c>; keeping the
/// <see cref="CaptureWidgetKind"/>-to-template decision here makes it unit-testable
/// without a UI.
/// </summary>
public enum FieldWidgetTemplate
{
    /// <summary>Free-text editor.</summary>
    Text,

    /// <summary>Numeric editor.</summary>
    Number,

    /// <summary>Boolean toggle editor.</summary>
    Toggle,

    /// <summary>Single/multi choice picker.</summary>
    Choice,

    /// <summary>Media capture (photo/video/audio/signature/sketch).</summary>
    Media,

    /// <summary>Barcode/QR scan editor.</summary>
    Barcode,

    /// <summary>Fallback editor for any other field.</summary>
    Fallback,
}

/// <summary>Pure mapping from a capture widget kind to its editor template.</summary>
public static class FieldWidgetTemplateMap
{
    /// <summary>
    /// Returns the editor template for a widget kind. Photo, video, audio,
    /// signature, and sketch all share the media editor; barcode, text, number,
    /// toggle, and choice each map to their own; everything else (date/time,
    /// file, location, record link, calculated, and any future kind) falls back.
    /// </summary>
    /// <param name="widget">The capture widget kind.</param>
    /// <returns>The editor template to render.</returns>
    public static FieldWidgetTemplate For(CaptureWidgetKind widget) => widget switch
    {
        CaptureWidgetKind.Text => FieldWidgetTemplate.Text,
        CaptureWidgetKind.Number => FieldWidgetTemplate.Number,
        CaptureWidgetKind.Toggle => FieldWidgetTemplate.Toggle,
        CaptureWidgetKind.Choice => FieldWidgetTemplate.Choice,
        CaptureWidgetKind.Photo or CaptureWidgetKind.Video or CaptureWidgetKind.Audio
            or CaptureWidgetKind.Signature or CaptureWidgetKind.Sketch => FieldWidgetTemplate.Media,
        CaptureWidgetKind.Barcode => FieldWidgetTemplate.Barcode,
        _ => FieldWidgetTemplate.Fallback,
    };
}
