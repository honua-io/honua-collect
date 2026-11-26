// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Field.Capture;
using Honua.Field.Presentation.Forms;

namespace Honua.Field.Presentation.Tests;

public class FieldWidgetTemplateMapTests
{
    [Theory]
    [InlineData(CaptureWidgetKind.Text, FieldWidgetTemplate.Text)]
    [InlineData(CaptureWidgetKind.Number, FieldWidgetTemplate.Number)]
    [InlineData(CaptureWidgetKind.Toggle, FieldWidgetTemplate.Toggle)]
    [InlineData(CaptureWidgetKind.Choice, FieldWidgetTemplate.Choice)]
    [InlineData(CaptureWidgetKind.Photo, FieldWidgetTemplate.Media)]
    [InlineData(CaptureWidgetKind.Video, FieldWidgetTemplate.Media)]
    [InlineData(CaptureWidgetKind.Audio, FieldWidgetTemplate.Media)]
    [InlineData(CaptureWidgetKind.Signature, FieldWidgetTemplate.Media)]
    [InlineData(CaptureWidgetKind.Sketch, FieldWidgetTemplate.Media)]
    [InlineData(CaptureWidgetKind.Barcode, FieldWidgetTemplate.Barcode)]
    [InlineData(CaptureWidgetKind.DateTime, FieldWidgetTemplate.Fallback)]
    [InlineData(CaptureWidgetKind.File, FieldWidgetTemplate.Fallback)]
    [InlineData(CaptureWidgetKind.Location, FieldWidgetTemplate.Fallback)]
    [InlineData(CaptureWidgetKind.RecordLink, FieldWidgetTemplate.RecordLink)]
    [InlineData(CaptureWidgetKind.Calculated, FieldWidgetTemplate.Fallback)]
    public void Maps_each_widget_kind_to_its_template(CaptureWidgetKind kind, FieldWidgetTemplate expected)
        => Assert.Equal(expected, FieldWidgetTemplateMap.For(kind));

    [Fact]
    public void Covers_every_capture_widget_kind()
    {
        // Guards against a new CaptureWidgetKind silently falling through untested:
        // every enum value must map to a defined template.
        foreach (CaptureWidgetKind kind in Enum.GetValues<CaptureWidgetKind>())
        {
            var template = FieldWidgetTemplateMap.For(kind);
            Assert.True(Enum.IsDefined(template));
        }
    }
}
