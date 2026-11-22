// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Forms;

public static class FormFieldTypes
{
    public static bool IsMedia(FormFieldType type)
        => type is FormFieldType.Photo or FormFieldType.Video or FormFieldType.Audio
            or FormFieldType.Signature or FormFieldType.Sketch or FormFieldType.File;
}
