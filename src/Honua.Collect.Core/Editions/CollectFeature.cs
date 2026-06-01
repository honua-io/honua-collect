namespace Honua.Collect.Core.Editions;

/// <summary>
/// Capabilities that may be gated behind a paid edition. The free Community
/// baseline (forms, offline sync, GPS/geometry capture, photo/signature) is not
/// represented here because it is never gated.
/// </summary>
public enum CollectFeature
{
    /// <summary>Per-record PDF/Word feature reports and bulk export.</summary>
    ReportsAndExports,

    /// <summary>Voice-to-fields, photo-to-fields, media redaction/face-blur.</summary>
    AiAssistedCapture,

    /// <summary>Manual conflict-review UI, selective sync, external high-accuracy GNSS.</summary>
    AdvancedSyncAndGis,

    /// <summary>SSO, role enforcement, audit logging, MDM/white-labeling.</summary>
    EnterpriseAuthAndAdmin,
}
