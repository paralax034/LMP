using Avalonia.Controls;

namespace LMP.UI.Dialogs.Content;

/// <summary>
/// Content for confirmation overlay dialogs (Yes/No, OK/Cancel).
/// 
/// <para><b>All properties are immutable</b> — set via constructor,
/// DataContext is set after field initialization,
/// ensuring XAML bindings correctly pick up values.</para>
/// </summary>
public partial class ConfirmDialogContent : UserControl
{
    /// <summary>Dialog title.</summary>
    public string Title { get; }

    /// <summary>Message text.</summary>
    public string Message { get; }

    /// <summary>Confirmation button text.</summary>
    public string ConfirmText { get; }

    /// <summary>Cancel button text.</summary>
    public string CancelText { get; }

    /// <summary>Confirm — invokes callback with <c>true</c>.</summary>
    public IRelayCommand ConfirmCommand { get; }

    /// <summary>Cancel — invokes callback with <c>false</c>.</summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>
    /// Constructor for the XAML compiler and previewer.
    /// Populates stub values to prevent designer crashes.
    /// </summary>
    public ConfirmDialogContent()
        : this("Designer Title", "Designer Message text...", "OK", "Cancel", _ => { })
    {
    }

    /// <param name="title">Dialog title.</param>
    /// <param name="message">Message text.</param>
    /// <param name="confirmText">Confirmation button text.</param>
    /// <param name="cancelText">Cancel button text.</param>
    /// <param name="onResult">Callback: <c>true</c> = confirmed, <c>false</c> = cancelled.</param>
    public ConfirmDialogContent(
        string title,
        string message,
        string confirmText,
        string cancelText,
        Action<bool> onResult)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        CancelText = cancelText;

        ConfirmCommand = new RelayCommand(() => onResult(true));
        CancelCommand = new RelayCommand(() => onResult(false));

        InitializeComponent();
        DataContext = this;
    }
}