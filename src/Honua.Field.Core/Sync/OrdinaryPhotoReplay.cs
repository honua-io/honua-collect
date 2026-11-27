// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using Honua.Field.Core.Records;

namespace Honua.Field.Core.Sync;

/// <summary>Checkpoints attachment uploads and reconciles ambiguous receipts without adding duplicates.</summary>
public static class OrdinaryPhotoReplay
{
    public static async Task<bool> ReplayAsync(
        FieldRecordEntry entry, GeoServicesFeatureSync sync, GeoServicesTarget target,
        string mediaDirectory, Func<FieldRecordEntry, Task> persist,
        CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(entry.RemoteId, NumberStyles.None, CultureInfo.InvariantCulture, out var objectId) || objectId <= 0)
        {
            return false;
        }

        var allSynced = true;
        foreach (var photo in entry.OrdinaryPhotos.Where(photo => photo.State != OrdinaryPhotoState.Synced))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (photo.Target != target)
            {
                allSynced = false;
                continue;
            }

            if (photo.State == OrdinaryPhotoState.NeedsVerification)
            {
                var verifiedId = await sync.FindMatchingAttachmentAsync(objectId, photo, cancellationToken).ConfigureAwait(false);
                if (verifiedId is null)
                {
                    allSynced = false;
                    continue;
                }

                await entry.UpdateOrdinaryPhotoAsync(photo with { State = OrdinaryPhotoState.Synced, ServerAttachmentId = verifiedId }, persist).ConfigureAwait(false);
                continue;
            }

            var name = Path.GetFileName(photo.LocalPath);
            if (name != photo.FileName || name != photo.LocalPath || string.IsNullOrEmpty(name))
            {
                allSynced = false;
                continue;
            }

            var path = Path.Combine(mediaDirectory, name);
            if (!File.Exists(path))
            {
                allSynced = false;
                continue;
            }

            using var file = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(hash, photo.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                allSynced = false;
                continue;
            }

            var held = photo with { State = OrdinaryPhotoState.NeedsVerification };
            await entry.UpdateOrdinaryPhotoAsync(held, persist).ConfigureAwait(false);
            var result = await sync.AddAttachmentAsync(objectId, path, photo.ContentType, target, cancellationToken).ConfigureAwait(false);
            if (result.Success && result.ObjectId is > 0)
            {
                await entry.UpdateOrdinaryPhotoAsync(held with { State = OrdinaryPhotoState.Synced, ServerAttachmentId = result.ObjectId }, persist).ConfigureAwait(false);
            }
            else
            {
                if (result.DefinitiveEditRejection)
                {
                    await entry.UpdateOrdinaryPhotoAsync(photo, persist).ConfigureAwait(false);
                }

                allSynced = false;
            }
        }

        return allSynced;
    }
}
