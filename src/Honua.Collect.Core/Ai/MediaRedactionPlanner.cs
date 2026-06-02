using Honua.Collect.Core.Field;
using Honua.Collect.Core.Field.Forms;

namespace Honua.Collect.Core.Ai;

/// <summary>
/// Finds the captured media that must be redacted before upload or export
/// (BACKLOG A3). Fields mark their attachments with
/// <c>RequiresFaceBlur</c> via the capture policy; this collects them so an
/// <see cref="IMediaRedactionProvider"/> can process them as a batch.
/// </summary>
public static class MediaRedactionPlanner
{
    /// <summary>Lists attachments on a form session that require redaction.</summary>
    /// <param name="session">Form session to scan.</param>
    /// <returns>Attachments flagged for redaction.</returns>
    public static IReadOnlyList<CapturedMediaAttachment> AttachmentsRequiringRedaction(FormSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.Fields
            .SelectMany(field => field.Media)
            .Where(media => media.RequiresFaceBlur)
            .ToList();
    }
}
