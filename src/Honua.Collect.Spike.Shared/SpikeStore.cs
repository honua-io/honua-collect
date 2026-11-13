using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Records;
using Honua.Collect.Core.Storage;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Spike.Shared;

// Spike fixture only. No server schema or new synchronization implementation.
public sealed class SpikeStore(string directory, Func<Task<string?>>? getKey = null)
{
    private Task<(SqliteRecordStore Inspections, SqliteRecordStore Assets)>? stores;
    private Task<(SqliteRecordStore Inspections, SqliteRecordStore Assets)> StoresAsync() => stores ??= OpenAsync();
    private async Task<(SqliteRecordStore, SqliteRecordStore)> OpenAsync()
    {
        Directory.CreateDirectory(directory);
        var key = getKey is null ? null : await getKey();
        return (new(Path.Combine(directory, "inspections.db"), key), new(Path.Combine(directory, "assets.db"), key));
    }
    public async Task<IReadOnlyList<CollectRecordEntry>> LoadAsync() => await (await StoresAsync()).Inspections.LoadAllAsync();
    public async Task<double[][]> PrepareAssetsAsync(Func<int, Task>? progress = null)
    {
        var assets = (await StoresAsync()).Assets;
        var records = await assets.LoadAllAsync();
        // Setup is excluded from the pan measurement. Stable IDs make interrupted preparation resumable.
        var existing = records.Select(x => x.Record.RecordId).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < 50_000; i++)
        {
            var id = $"asset-{i}";
            if (i % 250 == 0 && progress is not null) await progress(i);
            if (existing.Contains(id)) continue;
            await assets.SaveAsync(new CollectRecordEntry(new FieldRecord
            {
                RecordId = id, FormId = "shell-spike-assets",
                Location = new FieldGeoPoint(45.40 + i / 250 * 0.0012, -122.85 + i % 250 * 0.0015)
            }));
        }
        records = await assets.LoadAllAsync();
        return records.Select(x => new[] { x.Record.Location!.Longitude, x.Record.Location.Latitude }).ToArray();
    }
    public async Task<bool> QueueAsync(string name, string notes, string? photoPath = null)
    {
        var session = FormSession.CreateForNewRecord(new FormDefinition
        {
            FormId = "shell-spike-inspection", Name = "Inspection",
            Sections = [new FormSection { SectionId = "details", Label = "Details", Fields = [
                new FormField { FieldId = "name", Label = "Asset name", Type = FormFieldType.Text, Required = true },
                new FormField { FieldId = "notes", Label = "Notes", Type = FormFieldType.Text },
                new FormField { FieldId = "photo_path", Label = "Local photo path", Type = FormFieldType.Text }
            ] }]
        }, Guid.NewGuid().ToString("N"));
        session.SetValue("name", name);
        session.SetValue("notes", notes);
        if (photoPath is not null) session.SetValue("photo_path", photoPath);
        if (!session.MarkReadyToSubmit().IsValid || !session.Submit().IsValid) return false;
        var entry = new CollectRecordEntry(session.Record);
        entry.MarkPending();
        await (await StoresAsync()).Inspections.SaveAsync(entry);
        return true;
    }
}
