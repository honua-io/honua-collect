// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Sync;

namespace Honua.Field.Core.Records;

/// <summary>Upload state of a captured ordinary feature attachment.</summary>
public enum OrdinaryPhotoState
{
    Pending,
    NeedsVerification,
    Synced
}

/// <summary>Captured evidence and its frozen attachment destination.</summary>
public sealed record OrdinaryFeaturePhoto(
    Guid Id, string LocalPath, string ContentType, string FileName, string Sha256, GeoServicesTarget Target,
    OrdinaryPhotoState State = OrdinaryPhotoState.Pending, long? ServerAttachmentId = null);
