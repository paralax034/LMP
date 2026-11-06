using Avalonia.Controls;

namespace LMP.UI.Dialogs.Content;

/// <summary>
/// Content for the text input overlay dialog.
/// 
/// <para><b>Note:</b> Text is read directly from the TextBox
/// upon confirmation, bypassing two-way binding.
/// This is simpler and more reliable than INotifyPropertyChanged on the UserControl.</para>
/// </summary>
public partial class InputDialogContent : UserControl
{
    private static LocalizationService L => LocalizationService.Instance;

    /// <summary>Dialog title.</summary>
    public string Title { get; }

    /// <summary>Prompt text above the input field.</summary>
    public string Prompt { get; }

    /// <summary>Placeholder in the input field.</summary>
    public string Watermark { get; }

    /// <summary>Confirm button text.</summary>
    public string ConfirmText { get; }

    /// <summary>Cancel button text.</summary>
    public string CancelText { get; }

    /// <summary>Confirm input.</summary>
    public IRelayCommand ConfirmCommand { get; }

    /// <summary>Cancel input.</summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>
    /// Constructor for the XAML compiler and previewer.
    /// </summary>
    public InputDialogContent()
        : this("Input Title", "Please enter value:", "Placeholder...", _ => { })
    {
    }

    /// <param name="title">Title.</param>
    /// <param name="prompt">Prompt text above the input field.</param>
    /// <param name="watermark">Placeholder.</param>
    /// <param name="onResult">Callback: entered text or <c>null</c> on cancellation.</param>
    public InputDialogContent(
        string title,
        string prompt,
        string watermark,
        Action<string?> onResult)
    {
        Title = title;
        Prompt = prompt;
        Watermark = watermark;
        ConfirmText = L["Common_OK"];
        CancelText = L["Common_Cancel"];

        // Read text directly from the TextBox on confirmation —
        // no need for two-way binding or INotifyPropertyChanged
        ConfirmCommand = new RelayCommand(() =>
        {
            var textBox = this.FindControl<TextBox>("InputTextBox");
            onResult(textBox?.Text);
        });

        CancelCommand = new RelayCommand(() => onResult(null));

        InitializeComponent();
        DataContext = this;
    }
}