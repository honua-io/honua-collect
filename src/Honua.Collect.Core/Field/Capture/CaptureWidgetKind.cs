// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Capture;

public enum CaptureWidgetKind
{
    Text,

    Number,

    DateTime,

    Toggle,

    Choice,

    Photo,

    Video,

    Audio,

    Signature,

    Sketch,

    File,

    Barcode,

    Location,

    RecordLink,

    Calculated,
}

public static class CaptureWidget
{
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
