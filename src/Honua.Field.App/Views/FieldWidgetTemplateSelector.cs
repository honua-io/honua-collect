// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Presentation.Forms;

namespace Honua.Field.App.Views;

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

    /// <summary>Template for single choice fields.</summary>
    public DataTemplate? ChoiceTemplate { get; set; }

    /// <summary>Template for multiple choice fields.</summary>
    public DataTemplate? MultipleChoiceTemplate { get; set; }

    /// <summary>Template for capturing the device location.</summary>
    public DataTemplate? LocationTemplate { get; set; }

    /// <summary>Template for read-only calculated fields.</summary>
    public DataTemplate? CalculatedTemplate { get; set; }

    /// <summary>Template for media-capture fields (photo/video/audio/signature/sketch).</summary>
    public DataTemplate? MediaTemplate { get; set; }

    /// <summary>Template for barcode/QR scan fields.</summary>
    public DataTemplate? BarcodeTemplate { get; set; }

    /// <summary>Template for related-records link fields (BACKLOG F4).</summary>
    public DataTemplate? RecordLinkTemplate { get; set; }

    /// <summary>Fallback template for any other field.</summary>
    public DataTemplate? FallbackTemplate { get; set; }

    /// <inheritdoc />
    protected override DataTemplate? OnSelectTemplate(object item, BindableObject container)
    {
        if (item is not FieldViewModel field)
        {
            return FallbackTemplate;
        }

        return FieldWidgetTemplateMap.For(field.Widget) switch
        {
            FieldWidgetTemplate.Text => TextTemplate ?? FallbackTemplate,
            FieldWidgetTemplate.Number => NumberTemplate ?? FallbackTemplate,
            FieldWidgetTemplate.Toggle => ToggleTemplate ?? FallbackTemplate,
            FieldWidgetTemplate.Choice => ChoiceTemplate ?? FallbackTemplate,
            FieldWidgetTemplate.MultipleChoice => MultipleChoiceTemplate ?? FallbackTemplate,
            FieldWidgetTemplate.Location => LocationTemplate ?? FallbackTemplate,
            FieldWidgetTemplate.Calculated => CalculatedTemplate ?? FallbackTemplate,
            FieldWidgetTemplate.Media => MediaTemplate ?? FallbackTemplate,
            FieldWidgetTemplate.Barcode => BarcodeTemplate ?? FallbackTemplate,
            FieldWidgetTemplate.RecordLink => RecordLinkTemplate ?? FallbackTemplate,
            _ => FallbackTemplate,
        };
    }
}
