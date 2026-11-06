using Avalonia.Controls;

namespace LMP.UI.Dialogs.Content;

/// <summary>
/// Content for an overlay dialog with a custom set of buttons and an optional checkbox.
/// DataContext is set via DataTemplate in DialogHost.
/// </summary>
public partial class ChoiceDialogContent : UserControl
{
    public ChoiceDialogContent()
    {
        InitializeComponent();
    }
}