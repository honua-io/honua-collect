namespace Honua.Collect.Core.Automation;

/// <summary>
/// The guard on an automation rule. A condition is a pure, offline predicate over
/// the record's current field values — no arbitrary code, no network. Two flavours
/// ship: the data-driven <see cref="RuleCondition"/> (field/operator/comparand) and
/// the <see cref="ExpressionCondition"/>, which reuses the SDK form-expression
/// engine (the same language as field visibility/relevance) so authors get
/// <c>${a}='x' and ${b}&gt;5</c> for free.
/// </summary>
public interface IRuleCondition
{
    /// <summary>Evaluates the condition against the record's current values.</summary>
    /// <param name="values">The record's current values.</param>
    /// <returns><see langword="true"/> when the condition holds.</returns>
    bool Matches(IReadOnlyDictionary<string, object?> values);
}
