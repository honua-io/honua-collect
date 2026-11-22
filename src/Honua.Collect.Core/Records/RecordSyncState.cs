// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Records;

public enum RecordSyncState
{
    Local = 0,

    Pending = 1,

    Uploading = 2,

    Synced = 3,

    Failed = 4,

    Conflicted = 5,

    PendingUpdate = 6,
}
