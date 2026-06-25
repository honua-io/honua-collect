namespace Honua.Collect.Core.Migration;

/// <summary>
/// Thrown when a migration import cannot proceed because the source export is
/// structurally unusable (not valid JSON/CSV, missing the schema/fields the
/// importer needs). Per-field problems are reported non-fatally via
/// <see cref="MigratedForm.Skipped"/>; this is reserved for whole-input failures.
/// </summary>
public sealed class MigrationImportException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What made the import impossible.</param>
    public MigrationImportException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    /// <param name="message">What made the import impossible.</param>
    /// <param name="innerException">The underlying parse/IO failure.</param>
    public MigrationImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
