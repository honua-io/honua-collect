// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.App.Capture;
using Honua.Field.App.Services;
using Honua.Field.Core.Ai;
using Honua.Field.Core.Field.Annotation;
using Honua.Field.Core.Field.Capture;
using Honua.Field.Core.Field.Related;
using Honua.Field.Core.Storage;
using Honua.Field.Presentation.Forms;
using Honua.Sdk.Field.Records;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Field.App.Views;

/// <summary>
/// The dynamic form-capture page. It is a thin host: all behaviour lives in the
/// bound <see cref="FormPageViewModel"/> (unit-tested in
/// <c>Honua.Field.Presentation.Tests</c>), and the XAML renders each field via
/// the <see cref="FieldWidgetTemplateSelector"/>. Media widgets dispatch to the
/// device capture pipelines below by their <see cref="FieldViewModel.Widget"/>.
/// </summary>
public partial class FormPage : ContentPage
{
    private readonly HttpClient _aiHttp;
    private readonly IRelatedRecordStore? _relatedStore;
    private readonly RecordBook? _book;

    /// <summary>Creates the page for a form view-model.</summary>
    /// <param name="viewModel">The form to capture.</param>
    /// <param name="httpClientFactory">Factory supplying the configured AI HTTP client.</param>
    /// <param name="relatedStore">Optional durable store for record-link relationships (BACKLOG F4).</param>
    /// <param name="book">Optional record book used to persist newly created related child drafts.</param>
    public FormPage(
        FormPageViewModel viewModel,
        IHttpClientFactory httpClientFactory,
        IRelatedRecordStore? relatedStore = null,
        RecordBook? book = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _aiHttp = httpClientFactory.CreateClient(MauiProgram.AnthropicHttpClient);
        _relatedStore = relatedStore;
        _book = book;
        InitializeComponent();
#if IOS
        // Keep the floating Shell tab bar away from the editor actions. The
        // setting is scoped to this page, so the tabs return after navigation.
        Shell.SetTabBarIsVisible(this, false);
#endif
        BindingContext = viewModel;
    }

    private async void OnCaptureLocation(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: FieldViewModel field } button)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            var location = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(15)));
            if (location is null)
            {
                await DisplayAlert(field.Label, "A location fix is unavailable. Try again with a clear view of the sky.", "OK");
                return;
            }

            field.Value = ToGeotag(location).Location;
        }
        catch (PermissionException)
        {
            await DisplayAlert(field.Label, "Allow location access in device settings to capture this field.", "OK");
        }
        catch (FeatureNotEnabledException)
        {
            await DisplayAlert(field.Label, "Turn on device location services and try again.", "OK");
        }
        catch (FeatureNotSupportedException)
        {
            await DisplayAlert(field.Label, "Location capture is unavailable on this device.", "OK");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnCaptureMedia(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: FieldViewModel field })
        {
            return;
        }

        try
        {
            switch (field.Widget)
            {
                case CaptureWidgetKind.Photo:
                    await CapturePhotoAsync(field);
                    break;
                case CaptureWidgetKind.Video:
                    await CaptureVideoAsync(field);
                    break;
                case CaptureWidgetKind.Signature:
                    await CaptureInkAsync(field, "Signature");
                    break;
                case CaptureWidgetKind.Sketch:
                    await CaptureInkAsync(field, "Sketch");
                    break;
                case CaptureWidgetKind.Audio:
                    await CaptureAudioAsync(field);
                    break;
                case CaptureWidgetKind.File:
                    await PickFileAsync(field);
                    break;
                default:
                    await DisplayAlert(field.Label, $"{field.CaptureActionLabel} is not available yet.", "OK");
                    break;
            }
        }
        catch (FeatureNotSupportedException)
        {
            await DisplayAlert("Capture", "That capture type is not supported on this device.", "OK");
        }
        catch (PermissionException)
        {
            await DisplayAlert("Capture", "Permission to capture was denied.", "OK");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await DisplayAlert(field.Label, ex.Message, "OK");
        }
    }

    private async void OnScanBarcode(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: FieldViewModel field })
        {
            return;
        }

        try
        {
            var action = await DisplayActionSheet("Scan barcode", "Cancel", null, "Scan with camera", "Choose image");
            var image = action switch
            {
                "Scan with camera" => MediaPicker.Default.IsCaptureSupported
                    ? await MediaPicker.Default.CapturePhotoAsync()
                    : await MediaPicker.Default.PickPhotoAsync(),
                "Choose image" => await MediaPicker.Default.PickPhotoAsync(),
                _ => null,
            };

            if (image is null)
            {
                return;
            }

            var path = await CaptureFiles.ImportAsync(image);
            var code = await BarcodeDecoder.DecodeAsync(path);
            TryDelete(path);

            if (code is null)
            {
                await DisplayAlert(field.Label, "No barcode found in the image.", "OK");
                return;
            }

            field.Value = code;
        }
        catch (FeatureNotSupportedException)
        {
            await DisplayAlert("Scan", "Image capture is not supported on this device.", "OK");
        }
        catch (PermissionException)
        {
            await DisplayAlert("Scan", "Permission was denied.", "OK");
        }
    }

    private async void OnManageRelatedRecords(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: FieldViewModel field }
            || BindingContext is not FormPageViewModel vm)
        {
            return;
        }

        var related = vm.Session.GetRelated(field.FieldId);
        var relatedVm = new RelatedRecordsViewModel(related, _relatedStore);
        relatedVm.Changed += (_, _) => vm.RefreshFields();
        relatedVm.ChildCreated += async (_, child) =>
        {
            if (_book is not null)
            {
                await _book.AddSubmittedAsync(child);
            }
        };
        await Navigation.PushAsync(new RelatedRecordsPage(relatedVm));
    }

    private async void OnAiFill(object? sender, EventArgs e)
    {
        if (BindingContext is not FormPageViewModel vm)
        {
            return;
        }

        var apiKey = await SecureStorage.Default.GetAsync("anthropic_api_key");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await DisplayAlert("AI fill", "Set an Anthropic API key first (secure storage 'anthropic_api_key').", "OK");
            return;
        }

        try
        {
            var photo = MediaPicker.Default.IsCaptureSupported
                ? await MediaPicker.Default.CapturePhotoAsync()
                : await MediaPicker.Default.PickPhotoAsync();
            if (photo is null)
            {
                return;
            }

            var path = await CaptureFiles.ImportAsync(photo);
            var provider = new AnthropicPhotoToFieldsProvider(_aiHttp, new AnthropicPhotoToFieldsOptions { ApiKey = apiKey });
            var result = await provider.ExtractAsync(path, vm.Session.Form);
            TryDelete(path);

            var outcome = new AiCaptureService().Apply(vm.Session, result);
            vm.RefreshFields();
            await DisplayAlert("AI fill",
                outcome.Applied.Count > 0
                    ? $"Filled {outcome.Applied.Count} field(s) from the photo."
                    : $"No fields filled. {result.Unmapped}",
                "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("AI fill", $"AI fill failed: {ex.Message}", "OK");
        }
    }

    private async Task CapturePhotoAsync(FieldViewModel field)
    {
        var action = await DisplayActionSheet("Add photo", "Cancel", null, "Take photo", "Choose from gallery");

        // Only a photo taken now is geotagged with the device position; a gallery
        // pick may have been taken elsewhere and earlier.
        var capturedNow = action == "Take photo" && MediaPicker.Default.IsCaptureSupported;
        var photo = action switch
        {
            "Take photo" => capturedNow
                ? await MediaPicker.Default.CapturePhotoAsync()
                : await MediaPicker.Default.PickPhotoAsync(),
            "Choose from gallery" => await MediaPicker.Default.PickPhotoAsync(),
            _ => null,
        };

        if (photo is null)
        {
            return;
        }

        var (geotag, geoMeta) = capturedNow ? await TryCaptureGeotagAsync(field) : (null, null);

        // Land the original, downscale + re-encode (C8), and keep only the small copy.
        var imported = await CaptureFiles.ImportAsync(photo);
        var compressed = await ImageCompressor.CompressAsync(imported);
        TryDelete(imported);

        var finalPath = compressed;
        var contentType = "image/jpeg";
        PhotoAnnotationOverlay? overlay = null;
        var metadata = geoMeta ?? new MediaCaptureMetadata();

        // Offer markup/annotation (C7) — keep the original path in metadata so
        // annotations remain re-editable alongside the flattened evidence JPEG.
        if (await DisplayAlert("Photo", "Add markup to this photo?", "Annotate", "Skip"))
        {
            var annotated = await PhotoAnnotationPage.CaptureResultAsync(Navigation, compressed);
            if (annotated is not null)
            {
                finalPath = annotated.ImagePath;
                contentType = "image/jpeg";
                overlay = annotated.Overlay;
                metadata = metadata with { OriginalLocalPath = compressed };
            }
        }

        CaptureOrDiscard(
            () => field.CaptureMedia(
                finalPath,
                contentType: contentType,
                sizeBytes: TryGetFileSize(finalPath),
                location: geotag,
                metadata: metadata,
                annotationOverlay: overlay),
            finalPath,
            compressed);
    }

    private async Task CaptureVideoAsync(FieldViewModel field)
    {
        var action = await DisplayActionSheet("Add video", "Cancel", null, "Record video", "Choose from gallery");
        var video = action switch
        {
            "Record video" => await MediaPicker.Default.CaptureVideoAsync(),
            "Choose from gallery" => await MediaPicker.Default.PickVideoAsync(),
            _ => null,
        };

        if (video is null)
        {
            return;
        }

        // As for photos, only a video recorded now gets the device position.
        var (geotag, geoMeta) = action == "Record video" ? await TryCaptureGeotagAsync(field) : (null, null);
        var imported = await CaptureFiles.ImportAsync(video);
        CaptureOrDiscard(
            () => field.CaptureMedia(
                imported,
                contentType: video.ContentType ?? "video/mp4",
                sizeBytes: TryGetFileSize(imported),
                location: geotag,
                metadata: geoMeta),
            imported);
    }

    private static async Task PickFileAsync(FieldViewModel field)
    {
        var file = await FilePicker.Default.PickAsync();
        if (file is null)
        {
            return;
        }

        var path = await CaptureFiles.ImportAsync(file);
        CaptureOrDiscard(
            () => field.CaptureMedia(
                path,
                contentType: file.ContentType ?? "application/octet-stream",
                sizeBytes: TryGetFileSize(path)),
            path);
    }

    private async Task CaptureInkAsync(FieldViewModel field, string title)
    {
        var path = await InkCapturePage.CaptureAsync(Navigation, title);
        if (path is not null)
        {
            var (geotag, geoMeta) = await TryCaptureGeotagAsync(field);
            CaptureOrDiscard(
                () => field.CaptureMedia(
                    path,
                    contentType: "image/png",
                    sizeBytes: TryGetFileSize(path),
                    location: geotag,
                    metadata: geoMeta),
                path);
        }
    }

    private async Task CaptureAudioAsync(FieldViewModel field)
    {
        var result = await AudioRecorderPage.CaptureResultAsync(Navigation);
        if (result is not null)
        {
            var (geotag, geoMeta) = await TryCaptureGeotagAsync(field);
            var metadata = (geoMeta ?? new MediaCaptureMetadata()) with
            {
                DurationSeconds = result.Duration.TotalSeconds,
            };
            CaptureOrDiscard(
                () => field.CaptureMedia(
                    result.AudioPath,
                    contentType: "audio/wav",
                    sizeBytes: TryGetFileSize(result.AudioPath),
                    location: geotag,
                    metadata: metadata),
                result.AudioPath);
        }
    }

    private async void OnAnnotateMediaItem(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: MediaAttachmentItemViewModel item }
            || FindFieldForAttachment(item.AttachmentId) is not { } field)
        {
            return;
        }

        var sourcePath = item.Metadata?.OriginalLocalPath ?? item.LocalPath;
        if (!File.Exists(sourcePath))
        {
            await DisplayAlert("Markup", "The source image file could not be found.", "OK");
            return;
        }

        var result = await PhotoAnnotationPage.CaptureResultAsync(Navigation, sourcePath, item.AnnotationOverlay);
        if (result is null)
        {
            return;
        }

        var previousPath = item.LocalPath;
        try
        {
            field.AnnotateMedia(
                item.AttachmentId,
                result.Overlay,
                annotatedLocalPath: result.ImagePath,
                annotatedContentType: "image/jpeg",
                annotatedSizeBytes: TryGetFileSize(result.ImagePath));

            if (!string.Equals(previousPath, sourcePath, StringComparison.Ordinal)
                && !string.Equals(previousPath, result.ImagePath, StringComparison.Ordinal))
            {
                TryDeleteRemovedEvidence(previousPath);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            TryDelete(result.ImagePath);
            await DisplayAlert(field.Label, ex.Message, "OK");
        }
    }

    private async void OnEditMediaCaption(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: MediaAttachmentItemViewModel item }
            || FindFieldForAttachment(item.AttachmentId) is not { } field)
        {
            return;
        }

        var caption = await DisplayPromptAsync(
            "Media note",
            "Add or edit a caption for this attachment:",
            "Save",
            "Cancel",
            initialValue: item.Caption ?? string.Empty);

        if (caption is null)
        {
            return;
        }

        try
        {
            field.UpdateMediaCaption(item.AttachmentId, caption);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            // The attachment can be removed while the prompt is open.
            await DisplayAlert(field.Label, ex.Message, "OK");
        }
    }

    private async void OnRemoveMediaItem(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: MediaAttachmentItemViewModel item }
            || FindFieldForAttachment(item.AttachmentId) is not { } field)
        {
            return;
        }

        if (!await DisplayAlert("Remove attachment", $"Remove {item.FileName}?", "Remove", "Cancel"))
        {
            return;
        }

        if (field.RemoveMedia(item.AttachmentId))
        {
            TryDeleteRemovedEvidence(item.LocalPath);
            if (item.Metadata?.OriginalLocalPath is { } original)
            {
                TryDeleteRemovedEvidence(original);
            }
        }
    }

    private FieldViewModel? FindFieldForAttachment(string attachmentId)
    {
        if (BindingContext is not FormPageViewModel vm)
        {
            return null;
        }

        foreach (var field in vm.Fields)
        {
            if (field.HasAttachment(attachmentId))
            {
                return field;
            }
        }

        foreach (var group in vm.RepeatGroups)
        {
            foreach (var instance in group.Instances)
            {
                foreach (var field in instance.Fields)
                {
                    if (field.HasAttachment(attachmentId))
                    {
                        return field;
                    }
                }
            }
        }

        return null;
    }

    // A cached fix is used only when it is recent and precise; otherwise the
    // geotag would record where the device was, not where the media was captured.
    private static readonly TimeSpan MaxCachedFixAge = TimeSpan.FromSeconds(60);
    private const double MaxCachedFixAccuracyMeters = 50;

    private static async Task<(FieldGeoPoint? Location, MediaCaptureMetadata? Metadata)> TryCaptureGeotagAsync(
        FieldViewModel field)
    {
        if (!field.CapturesLocation)
        {
            return (null, null);
        }

        try
        {
            var location = await Geolocation.Default.GetLastKnownLocationAsync();
            if (!IsFreshFix(location))
            {
                location = await Geolocation.Default.GetLocationAsync(
                    new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(5)));
            }

            return location is null ? (null, null) : ToGeotag(location);
        }
        catch (Exception ex)
        {
            // Geotagging is best-effort; missing GPS hardware, disabled location
            // services or a denied permission must never block media capture.
            System.Diagnostics.Debug.WriteLine($"Geotag unavailable: {ex.GetType().Name}: {ex.Message}");
            return (null, null);
        }
    }

    private static bool IsFreshFix(Location? location) =>
        location is { Accuracy: { } accuracy }
        && accuracy is >= 0 and <= MaxCachedFixAccuracyMeters
        && DateTimeOffset.UtcNow - location.Timestamp <= MaxCachedFixAge;

    private static (FieldGeoPoint Location, MediaCaptureMetadata Metadata) ToGeotag(Location location)
    {
        // Devices report an unknown accuracy or altitude as a negative or non-finite
        // value; Core metadata validation rejects those, so they are dropped here.
        // Course is the direction of travel, not the camera heading, so it is not
        // recorded as BearingDegrees.
        var accuracy = location.Accuracy is { } a && double.IsFinite(a) && a >= 0 ? a : (double?)null;
        var altitude = location.Altitude is { } alt && double.IsFinite(alt) ? alt : (double?)null;
        var geoPoint = new FieldGeoPoint(location.Latitude, location.Longitude, accuracy);
        var metadata = new MediaCaptureMetadata
        {
            AltitudeMeters = altitude,
            AccuracyMeters = accuracy,
        };
        return (geoPoint, metadata);
    }

    private static void CaptureOrDiscard(Action capture, params string[] producedFiles)
    {
        try
        {
            capture();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The field's policy rejected the capture (count, content type, size or
            // metadata); the files made for it would otherwise stay on the device.
            foreach (var path in producedFiles.Distinct(StringComparer.Ordinal))
            {
                TryDelete(path);
            }

            throw;
        }
    }

    private static long? TryGetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void TryDeleteRemovedEvidence(string path)
    {
        // An offline draft or outbox entry can still reference a file removed
        // by the editor. Cleanup needs to account for all persisted references.
        if (BindingContext is FormPageViewModel { CanDeleteRemovedEvidence: true })
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the pre-compression original.
        }
    }
}
