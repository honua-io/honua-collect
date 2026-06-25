namespace Honua.Collect.Core.Automation;

/// <summary>Where an <see cref="OpenUrlAction"/> asks the host to open a URL.</summary>
public enum OpenUrlTarget
{
    /// <summary>Let the platform decide (system default — usually the external browser/app).</summary>
    Default,

    /// <summary>Open inside an in-app browser/web view (stays in the app).</summary>
    InApp,

    /// <summary>Open in the external system browser / handler app.</summary>
    External,
}

/// <summary>
/// A platform-neutral request to open a URL, produced by an <see cref="OpenUrlAction"/>
/// (BACKLOG #44 — "open URL"). Core only models the <em>intent</em>; the actual
/// launch is the host's job (MAUI <c>Launcher.OpenAsync</c> / <c>Browser.OpenAsync</c>),
/// so the runtime stays offline- and platform-agnostic and the action is fully
/// testable without a device.
/// </summary>
/// <param name="Url">The URL or scheme to open (http(s), tel:, mailto:, geo:, a deep link…).</param>
/// <param name="Target">Where to open it.</param>
/// <param name="RuleName">The rule that requested it.</param>
public sealed record OpenUrlIntent(string Url, OpenUrlTarget Target, string RuleName);
