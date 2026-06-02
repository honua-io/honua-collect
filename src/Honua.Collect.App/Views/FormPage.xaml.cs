using Honua.Collect.Presentation.Forms;

namespace Honua.Collect.App.Views;

/// <summary>
/// The dynamic form-capture page. It is a thin host: all behaviour lives in the
/// bound <see cref="FormPageViewModel"/> (unit-tested in
/// <c>Honua.Collect.Presentation.Tests</c>), and the XAML renders each field via
/// the <see cref="FieldWidgetTemplateSelector"/>.
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
            if (!MediaPicker.Default.IsCaptureSupported)
            {
                var picked = await MediaPicker.Default.PickPhotoAsync();
                if (picked is not null)
                {
                    field.CaptureMedia(picked.FullPath, picked.ContentType);
                }

                return;
            }

            var photo = await MediaPicker.Default.CapturePhotoAsync();
            if (photo is not null)
            {
                field.CaptureMedia(photo.FullPath, photo.ContentType);
            }
        }
        catch (FeatureNotSupportedException)
        {
            await DisplayAlert("Camera", "Media capture is not supported on this device.", "OK");
        }
        catch (PermissionException)
        {
            await DisplayAlert("Camera", "Permission to capture media was denied.", "OK");
        }
    }
}
