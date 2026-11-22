// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field;

public sealed class FieldCaptureWorkflow
{
    private readonly DuplicateDetector _duplicateDetector;

    public FieldCaptureWorkflow(DuplicateDetector duplicateDetector)
    {
        _duplicateDetector = duplicateDetector ?? throw new ArgumentNullException(nameof(duplicateDetector));
    }

    public void ApplyCalculatedFields(FormDefinition form, FieldRecord record)
        => CalculatedFieldEvaluator.ApplyCalculatedFields(form, record);

    public FormValidationResult Validate(FormDefinition form, FieldRecord record)
        => FormValidator.Validate(form, record);

    public bool CanTransition(RecordStatus from, RecordStatus to)
        => RecordWorkflow.CanTransition(from, to);

    public void Transition(FieldRecord record, RecordStatus targetStatus, DateTimeOffset? transitionTimeUtc = null)
        => RecordWorkflow.Transition(record, targetStatus, transitionTimeUtc);

    public IReadOnlyList<PotentialDuplicate> FindPotentialDuplicates(
        IEnumerable<FieldRecord> existing,
        FieldRecord candidate,
        DuplicateDetectionOptions? options = null)
        => _duplicateDetector.FindPotentialDuplicates(existing, candidate, options);
}
