// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.App.Capture;
using Honua.Field.App.Views;
using Honua.Field.Core.Enterprise;
using Honua.Field.Core.Field.Forms;
using Honua.Field.Core.Field.Related;
using Honua.Field.Core.Forms;
using Honua.Field.Core.Storage;
using Honua.Field.Presentation.Forms;

namespace Honua.Field.App.Services;

/// <summary>Opens new and resumed inspections with the same save and submit behavior.</summary>
public sealed class PublishedInspectionPages(AppSettings settings, RecordBook book,
    IHttpClientFactory clients, IRelatedRecordStore related, IAuthSessionStore sessions)
{
    /// <summary>Creates the inspection page using the selected published package.</summary>
    public FormPage Create(PublishedFormPackage package, FormSession session, Uri server, Action<string>? onSaved = null,
        bool isCorrection = false, string? draftPrincipal = null)
    {
        var principal = AuthHeaderHandler.CaptureReplayPrincipal(sessions);
        if ((isCorrection || draftPrincipal is not null) && principal != draftPrincipal)
        {
            throw new InvalidOperationException("Sign in with the account that captured this inspection before editing.");
        }

        var model = new FormPageViewModel(session, package.OfflineEnabled);
        var page = new FormPage(model, clients, related, book);
        var busy = false;
        PublishedFormSubmission? pending = null;
        string? attemptKey = null;

        async Task SaveAsync(Func<Task<string>> save)
        {
            if (busy)
            {
                return;
            }

            busy = true;
            page.IsEnabled = false;
            try
            {
                if (server != settings.BaseUri)
                {
                    model.RestoreDraftAfterSaveFailure();
                    throw new InvalidOperationException("Return to the server that supplied this form before saving.");
                }

                var message = await save();
                onSaved?.Invoke(message);
                await page.Navigation.PopAsync();
            }
            catch (Exception ex)
            {
                await page.DisplayAlert("Inspection", ex.Message, "OK");
            }
            finally
            {
                page.IsEnabled = true;
                busy = false;
            }
        }

        if (package.OfflineEnabled)
        {
            model.DraftSaved += async (_, record) => await SaveAsync(async () =>
            {
                var draft = PublishedFormDraft.Capture(package, session, server) with { ReplacesRejectedSubmission = isCorrection, ReplayPrincipal = principal };
                await book.SaveDraftAsync(record, draft);
                return "Draft saved. Open Records to continue.";
            });
        }

        model.SubmitSucceeded += async (_, record) => await SaveAsync(async () =>
        {
            if (package.OfflineEnabled)
            {
                try
                {
                    var snapshot = PublishedFormDraft.Capture(package, session, server) with { ReplayPrincipal = principal };
                    var key = isCorrection ? Guid.NewGuid().ToString("N") : null;
                    var submission = PublishedFormSubmission.Capture(package, session, server, idempotencyKey: key) with { ReplayPrincipal = principal };
                    await book.AddSubmittedAsync(record, submission, snapshot);
                }
                catch
                {
                    model.RestoreDraftAfterSaveFailure();
                    throw;
                }

                return "Saved to Outbox. Open Sync to upload.";
            }

            // If a response was lost, replay the identical request under the same idempotency key.
            pending ??= PublishedFormSubmission.Capture(package, session, server, idempotencyKey: attemptKey) with { ReplayPrincipal = principal };
            model.EditingEnabled = false;
            using var http = clients.CreateClient(pending.Files.Count > 0
                ? MauiProgram.UploadHttpClient
                : MauiProgram.ServerHttpClient);
            var receipt = await pending.ResolveMediaPaths(CaptureFiles.MediaDirectory).UploadForReplayAsync(http);
            if (!receipt.Success)
            {
                if (receipt.CanCorrectSubmission)
                {
                    pending = null;
                    attemptKey = Guid.NewGuid().ToString("N");
                    model.RestoreDraftAfterSaveFailure();
                    model.EditingEnabled = true;
                }

                throw new InvalidOperationException(receipt.Error);
            }

            return $"Submitted inspection {receipt.ObjectId}.";
        });

        return page;
    }
}
