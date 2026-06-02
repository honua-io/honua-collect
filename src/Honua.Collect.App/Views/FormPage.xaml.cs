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
}
