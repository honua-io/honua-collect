// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.App.Capture;
using Honua.Field.Core.Records;
using Honua.Field.Core.Storage;

namespace Honua.Field.App.Services;

/// <summary>Routes frozen form submissions through the shared durable Mobile outbox.</summary>
public sealed class PublishedOutboxReplay(LazyRecordStore recordStore, IHttpClientFactory clients)
{
    public async Task<bool> ReplayAsync(FieldRecordEntry entry, CancellationToken cancellationToken,
        string? reviewedLegacyPrincipal = null)
    {
        var store = await recordStore.GetStoreAsync(cancellationToken);
        if (store is not MobileRecordStore mobile)
        {
            throw new InvalidOperationException("Published outbox replay requires the Mobile record store.");
        }

        return await mobile.ReplayPublishedAsync(entry, async (submission, token) =>
        {
            using var http = clients.CreateClient(submission.Files.Count > 0
                ? MauiProgram.UploadHttpClient
                : MauiProgram.ServerHttpClient);
            if (submission.ReplayPrincipal is null && reviewedLegacyPrincipal is not null)
            {
                submission = submission with { ReplayPrincipal = reviewedLegacyPrincipal };
            }

            return await submission.UploadForReplayAsync(http, token);
        }, cancellationToken, CaptureFiles.MediaDirectory);
    }
}
