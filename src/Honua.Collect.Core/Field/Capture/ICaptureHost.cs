// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Forms;

public interface ICaptureHost
{
    FieldState GetField(string fieldId);

    void SetValue(string fieldId, object? value);

    void AddMedia(CapturedMediaAttachment attachment);

    bool RemoveMedia(string fieldId, string attachmentId);
}
