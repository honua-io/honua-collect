using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Migration;

/// <summary>
/// The result of importing an external form/data export into Collect (epic #37
/// migration guide): the mapped <see cref="FormDefinition"/>, any
/// <see cref="FieldRecord"/>s recovered from the export (empty for a schema-only
/// import), and a human-readable list of inputs that were
/// <see cref="Skipped"/> (unknown field types, system columns, malformed rows) so
/// the migration is auditable rather than silently lossy.
/// </summary>
/// <param name="Form">The mapped form definition.</param>
/// <param name="Records">Records recovered from the export, if any.</param>
/// <param name="Skipped">
/// Descriptions of fields/rows that could not be mapped and were skipped.
/// </param>
public sealed record MigratedForm(
    FormDefinition Form,
    IReadOnlyList<FieldRecord> Records,
    IReadOnlyList<string> Skipped);
