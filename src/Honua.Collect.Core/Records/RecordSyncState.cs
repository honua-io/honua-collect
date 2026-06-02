namespace Honua.Collect.Core.Records;

/// <summary>
/// Transport state of a captured record on the device, tracking its journey to
/// the server. This is deliberately <em>orthogonal</em> to the SDK's
/// <see cref="Sdk.Field.Records.RecordStatus"/>: that enum models the review
/// workflow (draft → submitted → approved/rejected), while this models whether
/// the bytes have actually reached the server yet.
/// </summary>
public enum RecordSyncState
{
    /// <summary>Held only on the device; not yet queued for upload.</summary>
    Local = 0,

    /// <summary>Queued for upload, waiting for connectivity or the next sync.</summary>
    Pending = 1,

    /// <summary>Currently uploading.</summary>
    Uploading = 2,

    /// <summary>Successfully uploaded and acknowledged by the server.</summary>
    Synced = 3,

    /// <summary>The last upload attempt failed; eligible for retry.</summary>
    Failed = 4,
}
