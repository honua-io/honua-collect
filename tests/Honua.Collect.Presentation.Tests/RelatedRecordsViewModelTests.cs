// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Field.Related;
using Honua.Collect.Presentation.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Tests;

/// <summary>
/// Covers the related-records (F4) view-model and field-level integration: creating,
/// linking, unlinking, durable SQLite persistence, hydration, and integrity policies.
/// </summary>
public sealed class RelatedRecordsViewModelTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRelatedRecordStore _store;

    public RelatedRecordsViewModelTests()
    {
        _dbPath = Path.GetTempFileName();
        _store = new SqliteRelatedRecordStore($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static FormDefinition CreateParentForm() => new()
    {
        FormId = "hydrant_inspection",
        Name = "Hydrant Inspection",
        Sections =
        [
            new FormSection
            {
                SectionId = "main",
                Title = "Main",
                Fields =
                [
                    new FormField
                    {
                        FieldId = "deficiencies",
                        Label = "Deficiencies",
                        Type = FormFieldType.RecordLink,
                        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["referencedFormId"] = "deficiency_report",
                        },
                    },
                ],
            },
        ],
    };

    [Fact]
    public async Task Create_link_unlink_round_trip_persists_to_store_and_updates_field_count()
    {
        var session = FormSession.CreateForNewRecord(
            CreateParentForm(),
            "parent-1",
            linkBehaviors: new Dictionary<string, RecordLinkBehavior> { ["deficiencies"] = RecordLinkBehavior.Restrict });
        var formVm = new FormPageViewModel(session);
        var linkFieldVm = Assert.Single(formVm.Fields);
        Assert.Equal("Manage related records", linkFieldVm.CaptureActionLabel);
        Assert.Equal(0, linkFieldVm.RelatedCount);

        var seq = 0;
        var vm = new RelatedRecordsViewModel(
            session.GetRelated("deficiencies"),
            _store,
            idGenerator: () => $"child-{++seq}");

        FieldRecord? createdEventRecord = null;
        var changedCount = 0;
        vm.ChildCreated += (_, r) => createdEventRecord = r;
        vm.Changed += (_, _) => changedCount++;

        Assert.Equal("Deficiencies", vm.Title);
        Assert.Equal("parent-1", vm.ParentRecordId);
        Assert.Equal("deficiencies", vm.FieldId);
        Assert.Equal("deficiency_report", vm.ReferencedFormId);
        Assert.Equal(RecordLinkBehavior.Restrict, vm.Behavior);
        Assert.Contains("Restrict", vm.BehaviorSummary);
        Assert.False(vm.HasChildren);

        vm.NewChildLabel = "Leaking gasket";
        var created = await vm.CreateChildAsync();
        Assert.Equal("child-1", created.RecordId);
        Assert.Same(created, createdEventRecord);
        Assert.Equal(string.Empty, vm.NewChildLabel);
        Assert.True(vm.HasChildren);
        Assert.Single(vm.Children);
        Assert.Equal("Leaking gasket", vm.Children[0].DisplayLabel);
        Assert.Contains("deficiency_report", vm.Children[0].Subtitle);

        formVm.RefreshFields();
        Assert.Equal(1, linkFieldVm.RelatedCount);

        // Link an existing record via bound properties.
        vm.ExistingRecordId = "existing-42";
        vm.ExistingRecordLabel = "Valve cap missing";
        Assert.True(await vm.LinkExistingAsync());
        Assert.Equal(2, vm.Children.Count);
        Assert.Equal(string.Empty, vm.ExistingRecordId);
        Assert.Equal(string.Empty, vm.ExistingRecordLabel);

        // Duplicate link is rejected cleanly.
        Assert.False(await vm.LinkExistingAsync("existing-42"));
        Assert.Contains("already linked", vm.StatusMessage);

        // Blank id is rejected cleanly.
        Assert.False(await vm.LinkExistingAsync("   "));
        Assert.Contains("Enter a child record ID", vm.StatusMessage);

        // Both links are durably stored in SQLite.
        var persisted = await _store.ListAsync("parent-1", "deficiencies");
        Assert.Equal(["child-1", "existing-42"], persisted.Select(l => l.RecordId));

        // Unlinking removes from both live model and SQLite store.
        var rowToRemove = vm.Children[0];
        Assert.True(await vm.UnlinkAsync(rowToRemove));
        Assert.Single(vm.Children);
        Assert.Equal("existing-42", vm.Children[0].RecordId);
        Assert.Equal(3, changedCount);

        var remaining = await _store.ListAsync("parent-1", "deficiencies");
        Assert.Equal(["existing-42"], remaining.Select(l => l.RecordId));

        // Unlinking null or already-removed row returns false cleanly.
        Assert.False(await vm.UnlinkAsync(null));
        Assert.False(await vm.UnlinkAsync(rowToRemove));
    }

    [Fact]
    public async Task LoadFromStoreAsync_hydrates_persisted_links_and_cascade_summary_formats()
    {
        await _store.LinkAsync(
            "parent-2",
            "deficiencies",
            new FieldRecordLinkValue { RecordId = "persisted-1", FormId = "deficiency_report", Label = null },
            RecordLinkBehavior.Cascade);

        var session = FormSession.CreateForNewRecord(CreateParentForm(), "parent-2");
        var vm = new RelatedRecordsViewModel(session.GetRelated("deficiencies"), _store);
        Assert.Contains("Cascade", vm.BehaviorSummary);
        Assert.Empty(vm.Children);

        await vm.LoadFromStoreAsync();

        var row = Assert.Single(vm.Children);
        Assert.Equal("persisted-1", row.RecordId);
        Assert.Equal("persisted-1", row.DisplayLabel);
        Assert.Contains("1 linked record(s)", vm.Header);
    }

    [Fact]
    public async Task Works_without_durable_store_and_commands_report_can_execute()
    {
        var session = FormSession.CreateForNewRecord(CreateParentForm(), "parent-3");
        var vm = new RelatedRecordsViewModel(session.GetRelated("deficiencies"));

        await vm.LoadFromStoreAsync(); // no-op without store
        Assert.True(vm.CreateChildCommand.CanExecute(null));
        Assert.True(vm.LinkExistingCommand.CanExecute(null));
        Assert.False(vm.UnlinkCommand.CanExecute(null));

        var child = await vm.CreateChildAsync("Cracked cap");
        var row = Assert.Single(vm.Children);
        Assert.Equal(child.RecordId, row.RecordId);
        Assert.True(vm.UnlinkCommand.CanExecute(row));
        Assert.True(await vm.UnlinkAsync(row));
        Assert.Empty(vm.Children);
    }
}
