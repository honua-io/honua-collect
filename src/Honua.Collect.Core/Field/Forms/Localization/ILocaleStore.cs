namespace Honua.Collect.Core.Field.Forms.Localization;

/// <summary>
/// Persists the user's active form language (BACKLOG F2 — multi-language /
/// localized forms) so the choice survives an app restart. The active locale is
/// stored per form id, mirroring Survey123/Fulcrum where a respondent's language
/// is remembered for the survey they last opened.
/// </summary>
public interface ILocaleStore
{
    /// <summary>Stores the active language for a form, replacing any prior value.</summary>
    /// <param name="formId">Form whose active language is being set.</param>
    /// <param name="language">BCP-47 language code (for example <c>es</c>, <c>fr-CA</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetActiveLanguageAsync(string formId, string language, CancellationToken ct = default);

    /// <summary>Reads the active language for a form, or <see langword="null"/> if none was ever set.</summary>
    /// <param name="formId">Form to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored BCP-47 language code, or <see langword="null"/>.</returns>
    Task<string?> GetActiveLanguageAsync(string formId, CancellationToken ct = default);
}
