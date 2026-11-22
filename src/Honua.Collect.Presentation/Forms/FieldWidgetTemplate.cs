// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Capture;

namespace Honua.Collect.Presentation.Forms;

public enum FieldWidgetTemplate
{
    Text,

    Number,

    Toggle,

    Choice,

    Media,

    Barcode,

    RecordLink,

    Fallback,
}

public static class FieldWidgetTemplateMap
{
    public static FieldWidgetTemplate For(CaptureWidgetKind widget) => widget switch
    {
        CaptureWidgetKind.Text => FieldWidgetTemplate.Text,
        CaptureWidgetKind.Number => FieldWidgetTemplate.Number,
        CaptureWidgetKind.Toggle => FieldWidgetTemplate.Toggle,
        CaptureWidgetKind.Choice => FieldWidgetTemplate.Choice,
        CaptureWidgetKind.Photo or CaptureWidgetKind.Video or CaptureWidgetKind.Audio
            or CaptureWidgetKind.Signature or CaptureWidgetKind.Sketch => FieldWidgetTemplate.Media,
        CaptureWidgetKind.Barcode => FieldWidgetTemplate.Barcode,
        CaptureWidgetKind.RecordLink => FieldWidgetTemplate.RecordLink,
        _ => FieldWidgetTemplate.Fallback,
    };
}
