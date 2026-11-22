// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Presentation.Forms;

namespace Honua.Collect.App.Views;

public sealed class FieldWidgetTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }

    public DataTemplate? NumberTemplate { get; set; }

    public DataTemplate? ToggleTemplate { get; set; }

    public DataTemplate? ChoiceTemplate { get; set; }

    public DataTemplate? MediaTemplate { get; set; }

    public DataTemplate? BarcodeTemplate { get; set; }

    public DataTemplate? RecordLinkTemplate { get; set; }

    public DataTemplate? FallbackTemplate { get; set; }

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
            FieldWidgetTemplate.Media => MediaTemplate ?? FallbackTemplate,
            FieldWidgetTemplate.Barcode => BarcodeTemplate ?? FallbackTemplate,
            FieldWidgetTemplate.RecordLink => RecordLinkTemplate ?? FallbackTemplate,
            _ => FallbackTemplate,
        };
    }
}
