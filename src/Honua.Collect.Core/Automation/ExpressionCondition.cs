using Honua.Sdk.Field.Forms.Expressions;

namespace Honua.Collect.Core.Automation;

/// <summary>
/// An automation condition expressed in the SDK form-expression language — the
/// same engine that drives field visibility / relevance (<c>${a}='x' and ${b}&gt;5</c>).
/// Reusing <see cref="ExpressionEvaluator"/> means automation conditions and form
/// relevance share one parser, one set of functions, and one truthiness model, so
/// there is no second expression language to learn or maintain. Evaluation is pure
/// and offline; a malformed or null result is treated as <see langword="false"/>.
/// </summary>
/// <param name="Expression">Boolean expression source text over the record's fields.</param>
public sealed record ExpressionCondition(string Expression) : IRuleCondition
{
    /// <inheritdoc />
    public bool Matches(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return ExpressionEvaluator.EvaluateBoolean(Expression, values);
    }
}
