// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Field.App.Services;

/// <summary>Serializes foreground uploads that mutate the shared record book.</summary>
internal static class ForegroundSyncGate
{
    internal static SemaphoreSlim Instance { get; } = new(1, 1);
}
