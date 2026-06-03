using Honua.Collect.App.Capture;
using Honua.Collect.Core.Field.Capture;
using Honua.Collect.Presentation.Forms;

namespace Honua.Collect.App.Views;

/// <summary>
/// The dynamic form-capture page. It is a thin host: all behaviour lives in the
/// bound <see cref="FormPageViewModel"/> (unit-tested in
/// <c>Honua.Collect.Presentation.Tests</c>), and the XAML renders each field via
/// the <see cref="FieldWidgetTemplateSelector"/>. Media widgets dispatch to the
/// device capture pipelines below by their <see cref="FieldViewModel.Widget"/>.
/// </summary>
public partial class FormPage : ContentPage
{
    /// <summary>Creates the page for a form view-model.</summary>
    /// <param name="viewModel">The form to capture.</param>
    public FormPage(FormPageViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
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

    private async Task CapturePhotoAsync(FieldViewModel field)
    {
        var action = await DisplayActionSheet("Add photo", "Cancel", null, "Take photo", "Choose from gallery");
        var photo = action switch
        {
            "Take photo" => MediaPicker.Default.IsCaptureSupported
                ? await MediaPicker.Default.CapturePhotoAsync()
                : await MediaPicker.Default.PickPhotoAsync(),
            "Choose from gallery" => await MediaPicker.Default.PickPhotoAsync(),
            _ => null,
        };

        if (photo is null)
        {
            return;
        }

        // Land the original, downscale + re-encode (C8), and keep only the small copy.
        var imported = await CaptureFiles.ImportAsync(photo);
        var compressed = await ImageCompressor.CompressAsync(imported);
        TryDelete(imported);
        field.CaptureMedia(compressed, "image/jpeg");
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

        var imported = await CaptureFiles.ImportAsync(video);
        field.CaptureMedia(imported, video.ContentType ?? "video/mp4");
    }

    private async Task CaptureInkAsync(FieldViewModel field, string title)
    {
        var path = await InkCapturePage.CaptureAsync(Navigation, title);
        if (path is not null)
        {
            field.CaptureMedia(path, "image/png");
        }
    }

    private async Task CaptureAudioAsync(FieldViewModel field)
    {
        var path = await AudioRecorderPage.CaptureAsync(Navigation);
        if (path is not null)
        {
            field.CaptureMedia(path, "audio/wav");
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
