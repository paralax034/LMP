using Avalonia.Controls;

namespace LMP.UI.Dialogs.Content;

/// <summary>
/// Content for an overlay dialog with an informational message (OK button only).
/// </summary>
public partial class InfoDialogContent : UserControl
{
    /// <summary>Dialog title.</summary>
    public string Title { get; }

    /// <summary>Message text.</summary>
    public string Message { get; }

    /// <summary>Close button text.</summary>
    public string ButtonText { get; }

    /// <summary>Close the dialog.</summary>
    public IRelayCommand CloseCommand { get; }

    /// <summary>
    /// Constructor for the XAML compiler and previewer.
    /// </summary>
    public InfoDialogContent()
        : this("Designer Title", "Info message for design time preview.", "OK", () => { })
    {
    }

    /// <param name="title">Dialog title.</param>
    /// <param name="message">Message text.</param>
    /// <param name="buttonText">Close button text.</param>
    /// <param name="onClose">Callback invoked when the button is clicked.</param>
    public InfoDialogContent(
        string title,
        string message,
        string buttonText,
        Action onClose)
    {
        Title = title;
        Message = message;
        ButtonText = buttonText;

        CloseCommand = new RelayCommand(onClose);

        InitializeComponent();
        DataContext = this;
    }
}