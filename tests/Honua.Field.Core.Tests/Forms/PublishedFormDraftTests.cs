// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Field;
using Honua.Field.Core.Field.Annotation;
using Honua.Field.Core.Field.Capture;
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
            "expressionLanguage": "xlsform",
            "offlinePolicy": {"enabled": true},
            "fields": [
              {"fieldId": "status", "type": "multiChoice"},
              {"fieldId": "where", "label": "Location", "type": "location", "required": true},
              {"fieldId": "date", "type": "date"},
              {"fieldId": "total", "type": "number"},
              {"fieldId": "photo", "type": "attachment"},
              {"fieldId": "note", "type": "text", "defaultValue": "walk-through"},
              {"fieldId": "rowPhoto", "type": "attachment"}
            ],
            "sections": [
              {"sectionId": "questions", "fieldIds": ["status", "date", "total"]},
              {"sectionId": "visits", "repeatable": true, "minInstances": 2, "fieldIds": ["note", "rowPhoto"]},
              {"sectionId": "final", "fieldIds": ["photo", "where"]}
            ]
          }
        }
        """;

    [Fact]
    public async Task Ios_upgrade_reopens_draft_and_replays_frozen_attachment_from_relocated_container()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var application = Path.Combine(root.FullName, "Application");
            var oldContainer = Path.Combine(application, Guid.NewGuid().ToString("D"));
            var newContainer = Path.Combine(application, Guid.NewGuid().ToString("D"));
            var media = Directory.CreateDirectory(Path.Combine(oldContainer, "Library", "media"));
            var oldPath = Path.Combine(media.FullName, $"{Guid.NewGuid():N}.jpg");
            await File.WriteAllBytesAsync(oldPath, [1, 2, 3]);
            var package = PublishedFormPackage.Parse("inspection", PackageJson);
            var session = package.CreateSession("stable-id");
            session.SetValue("where", new FieldGeoPoint(38, -122));
            var photo = new CapturedMediaAttachment { AttachmentId = "evidence", FieldId = "photo", LocalPath = oldPath };
            session.AddMedia(photo);
            var frozen = PublishedFormSubmission.Capture(package, session, Server);
            session.AddRepeatInstance("visits").AddMedia(photo with { AttachmentId = "repeat", FieldId = "rowPhoto" });
            var draft = PublishedFormDraft.Capture(package, session, Server);

            Directory.Move(oldContainer, newContainer);
            var newMedia = Path.Combine(newContainer, "Library", "media");
            var newPath = Path.Combine(newMedia, Path.GetFileName(oldPath));
            var (_, reopened) = draft.Open("inspection", "stable-id", Server, newMedia);
            Assert.Equal(newPath, Assert.Single(reopened.GetField("photo").Media).LocalPath);
            var repeat = Assert.Single(reopened.GetRepeat("visits").Instances);
            Assert.Equal(newPath, Assert.Single(repeat.GetField("rowPhoto").Media).LocalPath);
            var replay = frozen.ResolveMediaPaths(newMedia);
            Assert.Equal(frozen.RequestJson, replay.RequestJson);
            Assert.Equal(oldPath, Assert.Single(frozen.Files).LocalPath);
            Assert.Equal(Assert.Single(frozen.Files) with { LocalPath = newPath }, Assert.Single(replay.Files));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Assert.Single(replay.Files).LocalPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("/tmp/media/00000000000000000000000000000002.jpg")]
    [InlineData("/tmp/Application/20000000-0000-0000-0000-000000000000/Library/media/logo.jpg")]
    [InlineData("/tmp/Application/20000000-0000-0000-0000-000000000000/Library/media/../media/00000000000000000000000000000002.jpg")]
    public void Upgrade_leaves_paths_outside_the_capture_convention_untouched(string path)
    {
        const string newMedia = "/tmp/Application/10000000-0000-0000-0000-000000000000/Library/media";
        var frozen = new PublishedFormSubmission("inspection", "{}", [new SubmissionFile("file0", path, "image/jpeg", "photo.jpg")]);
        Assert.Equal(path, Assert.Single(frozen.ResolveMediaPaths(newMedia).Files).LocalPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Encrypted_draft_reopens_with_original_version_values_rows_and_media_then_is_replaced_on_submit(
        bool hasNewerCachedVersion)
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
            session.SetValue("where", location);
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
            var photoMetadata = new MediaCaptureMetadata { Caption = "Offline evidence", WidthPixels = 64 };
            var photoOverlay = new PhotoAnnotationOverlay([
                new PhotoAnnotation { Shape = AnnotationShape.Text, Points = [new(0.2, 0.3)], Text = "Inspection" },
            ]);
            photo = photoOverlay.ApplyTo(photoMetadata.ApplyTo(photo));
            session.AddMedia(photo);
            var first = session.AddRepeatInstance("visits");
            Assert.Equal("walk-through", first.GetValue("note"));
            first.SetValue("note", "first");
            var second = session.AddRepeatInstance("visits");
            Assert.Equal("walk-through", second.GetValue("note"));
            second.SetValue("note", null);
            second.AddMedia(photo with { AttachmentId = "evidence-2", FieldId = "rowPhoto" });

            var book = new RecordBook(new SqliteRecordStore(database, "draft-test-key"));
            await book.SaveDraftAsync(session.Record, PublishedFormDraft.Capture(package, session, Server));
            session.SetValue("total", 20.25m);
            await book.SaveDraftAsync(session.Record, PublishedFormDraft.Capture(package, session, Server));
            Assert.Single(book.All);
            selected[0] = "unsaved";
            session.SetValue("total", 99m);
            Assert.Equal(20.25m, Assert.Single(book.All).Record.Values["total"]);
            var warmDraft = Assert.IsType<PublishedFormDraft>(Assert.Single(book.All).PublishedDraft);
            var (_, warmSession) = warmDraft.Open("inspection", "stable-id", Server);
            var warmGroup = warmSession.GetRepeat("visits");
            Assert.Collection(warmGroup.Instances,
                row => Assert.Equal("first", row.GetValue("note")),
                row => Assert.Null(row.GetValue("note")));
            Assert.Equal("walk-through", warmGroup.AddInstance().GetValue("note"));
            if (hasNewerCachedVersion)
            {
                await File.WriteAllTextAsync(packageFile, """
                    {
                      "formId": "inspection", "version": 8, "status": "published",
                      "package": {
                        "formId": "inspection", "schemaVersion": "honua.form-package.v1",
                        "offlinePolicy": {"enabled": true},
                        "fields": [{"fieldId": "replacement", "type": "text"}]
                      }
                    }
                    """);
                var latest = PublishedFormPackage.LoadCached("inspection", packageFile)!;
                Assert.Equal(8, latest.Version);
                Assert.Equal("replacement", Assert.Single(Assert.Single(latest.Definition.Sections).Fields).FieldId);
            }
            else
            {
                File.Delete(packageFile);
            }

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
            Assert.Equal(location, Assert.IsType<FieldGeoPoint>(reopened.GetValue("where")));
            Assert.Equal(date, Assert.IsType<DateOnly>(reopened.GetValue("date")));
            Assert.Equal(20.25m, Assert.IsType<decimal>(reopened.GetValue("total")));
            var restoredPhoto = Assert.Single(reopened.GetField("photo").Media);
            Assert.Equal(photo.AttachmentId, restoredPhoto.AttachmentId);
            Assert.Equal(photo.CaptureLocation, restoredPhoto.CaptureLocation);
            Assert.Equal(photoMetadata, MediaCaptureMetadata.From(restoredPhoto));
            Assert.Equal("Inspection", Assert.Single(PhotoAnnotationOverlay.From(restoredPhoto).Annotations).Text);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(restoredPhoto.LocalPath));
            var group = reopened.GetRepeat("visits");
            Assert.Equal(2, group.Bounds?.Min);
            Assert.Collection(group.Instances,
                row => Assert.Equal("first", row.GetValue("note")),
                row =>
                {
                    Assert.Null(row.GetValue("note"));
                    Assert.Equal("evidence-2", Assert.Single(row.GetField("rowPhoto").Media).AttachmentId);
                });

            var added = group.AddInstance();
            Assert.Equal("walk-through", added.GetValue("note"));
            Assert.True(group.RemoveInstance(added.InstanceId));
            Assert.True(reopened.Validate().IsValid);
            reopened.Record.Status = RecordStatus.Submitted;
            var snapshot = PublishedFormDraft.Capture(reopenedPackage, reopened, Server);
            var original = new PublishedFormSubmission("inspection", """{"idempotencyKey":"stable-id"}""", [], Server.AbsoluteUri);
            var submitted = await reopenedBook.AddSubmittedAsync(reopened.Record, original, snapshot);
            Assert.Same(submitted, Assert.Single(reopenedBook.All));
            Assert.Equal(RecordSyncState.Pending, submitted.SyncState);
            Assert.False(submitted.CanOpenPublishedEditor);

            submitted.MarkSubmissionRejected("Correct the site name.");
            await reopenedBook.SaveAsync(submitted);
            var persisted = Assert.Single(await new SqliteRecordStore(database, "draft-test-key").LoadAllAsync());
            Assert.Equal(RecordSyncState.SubmissionRejected, persisted.SyncState);
            Assert.Equal("Correct the site name.", persisted.LastError);
            Assert.False(persisted.IsPendingUpload);
            Assert.True(persisted.CanOpenPublishedEditor);
            Assert.Equal(original.RequestJson, persisted.PublishedSubmission?.RequestJson);

            var (correctionPackage, correction) = persisted.PublishedDraft!.Open(persisted.Record.FormId, persisted.Record.RecordId, Server);
            Assert.Equal(7, correctionPackage.Version);
            Assert.True(correction.Validate().IsValid);
            Assert.Equal(location, correction.GetValue("where"));
            Assert.Equal(new[] { "open", "closed" }, Assert.IsType<string[]>(correction.GetValue("status")));
            Assert.Equal(photo.AttachmentId, Assert.Single(correction.GetField("photo").Media).AttachmentId);
            Assert.Null(correction.GetRepeat("visits").Instances[1].GetValue("note"));

            var correctionDraft = PublishedFormDraft.Capture(correctionPackage, correction, Server) with { ReplacesRejectedSubmission = true };
            await reopenedBook.SaveDraftAsync(correction.Record, correctionDraft);
            persisted = Assert.Single(await new SqliteRecordStore(database, "draft-test-key").LoadAllAsync());
            Assert.Null(persisted.PublishedSubmission);
            Assert.True(persisted.CanOpenPublishedEditor);
            Assert.True(persisted.PublishedDraft!.ReplacesRejectedSubmission);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
