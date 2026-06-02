namespace Honua.Collect.Core.Records;

/// <summary>
/// The mailbox a record appears in, mirroring the Drafts / Outbox / Sent boxes
/// users of Survey123 and Fulcrum expect (BACKLOG S4). The box is <em>derived</em>
/// from the record's review status and transport <see cref="RecordSyncState"/>;
/// it is never stored independently.
/// </summary>
public enum RecordBox
{
    /// <summary>Still being edited and not yet finished (review status Draft).</summary>
    Drafts,

    /// <summary>Finished and awaiting (or retrying) upload to the server.</summary>
    Outbox,

    /// <summary>Successfully uploaded to the server.</summary>
    Sent,
}
