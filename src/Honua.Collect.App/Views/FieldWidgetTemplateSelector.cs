using Honua.Collect.Core.Field.Capture;
using Honua.Collect.Presentation.Forms;

namespace Honua.Collect.App.Views;

/// <summary>
/// Chooses the editor <see cref="DataTemplate"/> for a <see cref="FieldViewModel"/>
/// from its <see cref="FieldViewModel.Widget"/>. The dynamic form renderer uses
/// this so each field type (text, number, toggle, choice, media, …) presents the
/// right control while all binding still flows through the tested view-model.
/// </summary>
public sealed class FieldWidgetTemplateSelector : DataTemplateSelector
{
    /// <summary>Template for free-text fields.</summary>
    public DataTemplate? TextTemplate { get; set; }

    /// <summary>Template for numeric fields.</summary>
    public DataTemplate? NumberTemplate { get; set; }

    /// <summary>Template for boolean toggle fields.</summary>
    public DataTemplate? ToggleTemplate { get; set; }

    /// <summary>Template for single/multi choice fields.</summary>
    public DataTemplate? ChoiceTemplate { get; set; }

    /// <summary>Template for media-capture fields (photo/video/audio/signature/sketch).</summary>
    public DataTemplate? MediaTemplate { get; set; }

    /// <summary>Fallback template for any other field.</summary>
    public DataTemplate? FallbackTemplate { get; set; }

    /// <inheritdoc />
    protected override DataTemplate? OnSelectTemplate(object item, BindableObject container)
    {
        if (item is not FieldViewModel field)
        {
            return FallbackTemplate;
        }

        return field.Widget switch
        {
            CaptureWidgetKind.Text => TextTemplate ?? FallbackTemplate,
            CaptureWidgetKind.Number => NumberTemplate ?? FallbackTemplate,
            CaptureWidgetKind.Toggle => ToggleTemplate ?? FallbackTemplate,
            CaptureWidgetKind.Choice => ChoiceTemplate ?? FallbackTemplate,
            CaptureWidgetKind.Photo or CaptureWidgetKind.Video or CaptureWidgetKind.Audio
                or CaptureWidgetKind.Signature or CaptureWidgetKind.Sketch => MediaTemplate ?? FallbackTemplate,
            _ => FallbackTemplate,
        };
    }
}
