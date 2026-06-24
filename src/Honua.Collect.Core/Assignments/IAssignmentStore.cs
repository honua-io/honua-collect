namespace Honua.Collect.Core.Assignments;

/// <summary>
/// Durable, device-local store for dispatched <see cref="FieldAssignment"/>s so a
/// worker's inbox survives app restarts and offline periods (BACKLOG E5). Mirrors
/// the <see cref="Storage.IRecordStore"/> seam: assignments are pulled from the
/// server, persisted here, and their lifecycle status is pushed back on sync.
/// </summary>
public interface IAssignmentStore
{
    /// <summary>
    /// Inserts the assignment, or updates the existing row with the same
    /// <see cref="FieldAssignment.AssignmentId"/> (its current status and links).
    /// </summary>
    /// <param name="assignment">The assignment to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(FieldAssignment assignment, CancellationToken ct = default);

    /// <summary>Loads every stored assignment in its persisted lifecycle state.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All persisted assignments.</returns>
    Task<IReadOnlyList<FieldAssignment>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>Loads the assignments dispatched to a single operator.</summary>
    /// <param name="userId">The operator whose assignments to load.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operator's persisted assignments.</returns>
    Task<IReadOnlyList<FieldAssignment>> LoadForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>Removes the assignment with the given id, if present.</summary>
    /// <param name="assignmentId">The assignment id to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string assignmentId, CancellationToken ct = default);
}
