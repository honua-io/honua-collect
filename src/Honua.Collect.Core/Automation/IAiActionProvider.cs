namespace Honua.Collect.Core.Automation;

/// <summary>The context handed to an AI action when it runs as an automation step.</summary>
/// <param name="ActionId">The registered action being invoked.</param>
/// <param name="Prompt">Optional prompt/instruction from the rule.</param>
/// <param name="Values">The record's current field values at the point of invocation.</param>
public sealed record AiActionRequest(
    string ActionId,
    string? Prompt,
    IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// What an AI action produced: field writes to fold back into the record, plus an
/// optional human-readable note surfaced as an informational alert. An action that
/// declines to act returns <see cref="Skipped"/>.
/// </summary>
/// <param name="FieldWrites">Field values the action wants to set.</param>
/// <param name="Note">Optional note surfaced to the user.</param>
public sealed record AiActionResult(
    IReadOnlyDictionary<string, object?> FieldWrites,
    string? Note = null)
{
    /// <summary>An empty result — the action ran but made no changes.</summary>
    public static AiActionResult Skipped { get; } =
        new(new Dictionary<string, object?>(StringComparer.Ordinal));
}

/// <summary>
/// The seam through which the automation runtime invokes on-device AI actions
/// (BACKLOG #44). Implementations are resolved by <see cref="AiActionRequest.ActionId"/>
/// and run fully offline. Honua ships <see cref="NoOpAiActionProvider"/> — a stub
/// that exercises the seam without any model or network — so the engine and its
/// tests are provider-independent. A live, on-device provider (Bedrock/OpenAI per
/// the server AI providers, the on-device model of #41, CV of #42) is the deferred
/// follow-up that plugs in here without touching the runtime.
/// </summary>
public interface IAiActionProvider
{
    /// <summary>Whether this provider can service the given action id.</summary>
    /// <param name="actionId">The action identifier from the rule.</param>
    /// <returns><see langword="true"/> when <see cref="Run"/> can handle it.</returns>
    bool CanHandle(string actionId);

    /// <summary>Runs the AI action against the record context.</summary>
    /// <param name="request">The invocation context.</param>
    /// <returns>The field writes and optional note the action produced.</returns>
    AiActionResult Run(AiActionRequest request);
}

/// <summary>
/// The default AI-action provider: a deterministic no-op. It claims no actions and,
/// if ever asked to run one, returns <see cref="AiActionResult.Skipped"/>. This is
/// the shipped stub for the deferred live provider — it proves the
/// <see cref="RunAiAction"/> seam fires end to end (the runtime records that the AI
/// step was reached) without any model, file, or network access.
/// </summary>
public sealed class NoOpAiActionProvider : IAiActionProvider
{
    /// <inheritdoc />
    public bool CanHandle(string actionId) => false;

    /// <inheritdoc />
    public AiActionResult Run(AiActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AiActionResult.Skipped;
    }
}
