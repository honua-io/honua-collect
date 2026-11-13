using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Records;
using Honua.Collect.Core.Storage;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Spike.Shared;

// Spike fixture only. No server schema or new synchronization implementation.
public sealed class SpikeStore(string directory, string? encryptionKey = null)
{
    private readonly SqliteRecordStore store = new(Path.Combine(directory, "inspections.db"), encryptionKey);
    public Task<IReadOnlyList<CollectRecordEntry>> LoadAsync() => store.LoadAllAsync();
    public async Task<bool> QueueAsync(string name, string notes)
    {
        var session = FormSession.CreateForNewRecord(new FormDefinition
        {
            FormId = "shell-spike-inspection", Name = "Inspection",
            Sections = [new FormSection { SectionId = "details", Label = "Details", Fields = [
                new FormField { FieldId = "name", Label = "Asset name", Type = FormFieldType.Text, Required = true },
                new FormField { FieldId = "notes", Label = "Notes", Type = FormFieldType.Text }
            ] }]
        }, Guid.NewGuid().ToString("N"));
        session.SetValue("name", name);
        session.SetValue("notes", notes);
        if (!session.MarkReadyToSubmit().IsValid || !session.Submit().IsValid) return false;
        var entry = new CollectRecordEntry(session.Record);
        entry.MarkPending();
        await store.SaveAsync(entry);
        return true;
    }
}
