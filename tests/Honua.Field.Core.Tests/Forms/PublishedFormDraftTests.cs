// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Field;
using Honua.Field.Core.Forms;
using Honua.Field.Core.Records;
using Honua.Field.Core.Storage;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Core.Tests.Forms;

public sealed class PublishedFormDraftTests
{
    private static readonly Uri Server = new("https://example.com");
    private const string PackageJson = """
        {
          "formId": "inspection", "version": 7, "status": "published",
          "package": {
            "formId": "inspection", "schemaVersion": "honua.form-package.v1",
            "offlinePolicy": {"enabled": true},
            "fields": [
              {"fieldId": "status", "type": "multiChoice"},
              {"fieldId": "location", "type": "geometry"},
              {"fieldId": "date", "type": "date"},
              {"fieldId": "total", "type": "number"},
              {"fieldId": "photo", "type": "attachment"},
              {"fieldId": "note", "type": "text"},
              {"fieldId": "rowPhoto", "type": "attachment"}
            ],
            "sections": [
              {"sectionId": "visits", "repeatable": true, "minInstances": 2, "fieldIds": ["note", "rowPhoto"]}
            ]
          }
        }
        """;

    [Fact]
    public async Task Encrypted_draft_reopens_with_original_version_values_rows_and_media_then_is_replaced_on_submit()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var packageFile = Path.Combine(directory.FullName, "package.json");
            var database = Path.Combine(directory.FullName, "records.db");
            var photoPath = Path.Combine(directory.FullName, "inspection.jpg");
            await File.WriteAllTextAsync(packageFile, PackageJson);
            await File.WriteAllBytesAsync(photoPath, [1, 2, 3]);
            var package = PublishedFormPackage.LoadCached("inspection", packageFile)!;
            var session = package.CreateSession("stable-id");
            string[] selected = ["open", "closed"];
            session.SetValue("status", selected);
            var location = new FieldGeoPoint(38.02, -122.17, 3.0);
            session.SetValue("location", location);
            var date = new DateOnly(2026, 11, 26);
            session.SetValue("date", date);
            session.SetValue("total", 19.75m);
            var photo = new CapturedMediaAttachment
            {
                AttachmentId = "evidence-1",
                FieldId = "photo",
                LocalPath = photoPath,
                ContentType = "image/jpeg",
                SizeBytes = 3,
                CaptureLocation = location,
            };
            session.AddMedia(photo);
            session.AddRepeatInstance("visits").SetValue("note", "first");
            var second = session.AddRepeatInstance("visits");
            second.SetValue("note", "second");
            second.AddMedia(photo with { AttachmentId = "evidence-2", FieldId = "rowPhoto" });

            var book = new RecordBook(new SqliteRecordStore(database, "draft-test-key"));
            await book.SaveDraftAsync(session.Record, PublishedFormDraft.Capture(package, session, Server));
            session.SetValue("total", 20.25m);
            await book.SaveDraftAsync(session.Record, PublishedFormDraft.Capture(package, session, Server));
            Assert.Single(book.All);
            selected[0] = "unsaved";
            session.SetValue("total", 99m);
            Assert.Equal(20.25m, Assert.Single(book.All).Record.Values["total"]);
            File.Delete(packageFile);

            var reopenedBook = new RecordBook(new SqliteRecordStore(database, "draft-test-key"));
            await reopenedBook.InitializeAsync();
            var entry = Assert.Single(reopenedBook.All);
            Assert.Equal(RecordSyncState.Local, entry.SyncState);
            var draft = Assert.IsType<PublishedFormDraft>(entry.PublishedDraft);
            Assert.Throws<InvalidOperationException>(() => draft.Open("inspection", "stable-id", new Uri("https://other.example")));
            var (reopenedPackage, reopened) = draft.Open("inspection", "stable-id", Server);

            Assert.Equal(7, reopenedPackage.Version);
            Assert.Equal(session.Record.CreatedAtUtc, reopened.Record.CreatedAtUtc);
            Assert.Equal("stable-id", reopened.Record.RecordId);
            Assert.Equal(new[] { "open", "closed" }, Assert.IsType<string[]>(reopened.GetValue("status")));
            Assert.Equal(location, Assert.IsType<FieldGeoPoint>(reopened.GetValue("location")));
            Assert.Equal(date, Assert.IsType<DateOnly>(reopened.GetValue("date")));
            Assert.Equal(20.25m, Assert.IsType<decimal>(reopened.GetValue("total")));
            var restoredPhoto = Assert.Single(reopened.GetField("photo").Media);
            Assert.Equal(photo.AttachmentId, restoredPhoto.AttachmentId);
            Assert.Equal(photo.CaptureLocation, restoredPhoto.CaptureLocation);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(restoredPhoto.LocalPath));
            var group = reopened.GetRepeat("visits");
            Assert.Equal(2, group.Bounds?.Min);
            Assert.Collection(group.Instances,
                row => Assert.Equal("first", row.GetValue("note")),
                row =>
                {
                    Assert.Equal("second", row.GetValue("note"));
                    Assert.Equal("evidence-2", Assert.Single(row.GetField("rowPhoto").Media).AttachmentId);
                });

            reopened.Record.Status = RecordStatus.Submitted;
            var submitted = await reopenedBook.AddSubmittedAsync(reopened.Record, new("inspection", "{}", []));
            Assert.Same(submitted, Assert.Single(reopenedBook.All));
            Assert.Equal(RecordSyncState.Pending, submitted.SyncState);
            Assert.Null(submitted.PublishedDraft);
            var persisted = Assert.Single(await new SqliteRecordStore(database, "draft-test-key").LoadAllAsync());
            Assert.NotNull(persisted.PublishedSubmission);
            Assert.Null(persisted.PublishedDraft);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
