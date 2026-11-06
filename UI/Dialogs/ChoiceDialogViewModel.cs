namespace LMP.UI.Dialogs;

/// <summary>
/// Description of a single button in a choice dialog.
/// </summary>
/// <typeparam name="T">The type of the value returned when clicked.</typeparam>
public sealed class ChoiceOption<T>
{
    /// <summary>Button text.</summary>
    public required string Text { get; init; }

    /// <summary>Value returned when this button is clicked.</summary>
    public required T Value { get; init; }

    /// <summary>
    /// True if the button is primary (accented). Default is false.
    /// </summary>
    public bool IsPrimary { get; init; }
}

/// <summary>
/// ViewModel for a single button in the UI. Non-generic for DataTemplate compatibility.
/// </summary>
public sealed class ChoiceButtonViewModel
{
    /// <summary>Button text.</summary>
    public string Text { get; }

    /// <summary>True if the button is primary (accented).</summary>
    public bool IsPrimary { get; }

    /// <summary>Click command.</summary>
    public IRelayCommand ClickCommand { get; }

    public ChoiceButtonViewModel(string text, bool isPrimary, Action onClick)
    {
        Text = text;
        IsPrimary = isPrimary;
        ClickCommand = new RelayCommand(onClick);
    }
}

/// <summary>
/// Universal ViewModel for an overlay dialog with an arbitrary set of buttons
/// and an optional checkbox.
/// 
/// <para><b>Usage:</b></para>
/// <code>
/// var vm = ChoiceDialogViewModel.Create(
///     title: "Close?",
///     message: "What to do?",
///     options: [
///         new() { Text = "Minimize", Value = CloseAction.MinimizeToTray, IsPrimary = true },
///         new() { Text = "Exit", Value = CloseAction.Exit }
///     ],
///     checkBoxText: "Remember",
///     onResult: (value, isChecked) => { ... },
///     onCancel: () => { ... }
/// );
/// </code>
/// 
/// <para>The Cancel button is added automatically if <c>cancelText</c> is specified.</para>
/// </summary>
public sealed partial class ChoiceDialogViewModel : ObservableObject
{
    /// <summary>Dialog title.</summary>
    public string Title { get; }

    /// <summary>Message text (can be null/empty — hidden in that case).</summary>
    public string? Message { get; }

    /// <summary>Checkbox text. Null = checkbox is hidden.</summary>
    public string? CheckBoxText { get; }

    /// <summary>Indicates whether a checkbox is present.</summary>
    public bool HasCheckBox => !string.IsNullOrEmpty(CheckBoxText);

    /// <summary>Current state of the checkbox.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>List of buttons to display.</summary>
    public IReadOnlyList<ChoiceButtonViewModel> Buttons { get; }

    private ChoiceDialogViewModel(
        string title,
        string? message,
        string? checkBoxText,
        IReadOnlyList<ChoiceButtonViewModel> buttons)
    {
        Title = title;
        Message = message;
        CheckBoxText = checkBoxText;
        Buttons = buttons;
    }

    /// <summary>
    /// Factory method for creating a strongly-typed choice dialog.
    /// 
    /// <para>Generates a <see cref="ChoiceDialogViewModel"/> (non-generic)
    /// for DataTemplate compatibility, while preserving strongly-typed callbacks.</para>
    /// </summary>
    /// <typeparam name="T">Type of the button values.</typeparam>
    /// <param name="title">Title.</param>
    /// <param name="message">Message text (optional).</param>
    /// <param name="options">List of buttons with their values.</param>
    /// <param name="onResult">
    /// Callback invoked when any button (except Cancel) is clicked.
    /// Parameters: (value, isCheckBoxChecked).
    /// </param>
    /// <param name="onCancel">Callback invoked on cancellation (optional).</param>
    /// <param name="cancelText">
    /// Cancel button text. If specified, the Cancel button is added automatically.
    /// If null, no Cancel button is added.
    /// </param>
    /// <param name="checkBoxText">Checkbox text. Null — checkbox is hidden.</param>
    public static ChoiceDialogViewModel Create<T>(
        string title,
        string? message,
        IReadOnlyList<ChoiceOption<T>> options,
        Action<T, bool> onResult,
        Action? onCancel = null,
        string? cancelText = null,
        string? checkBoxText = null)
    {
        // Reference to the future VM to access IsChecked from buttons
        ChoiceDialogViewModel? vmRef = null;

        var buttons = new List<ChoiceButtonViewModel>(options.Count + (cancelText != null ? 1 : 0));

        foreach (var opt in options)
        {
            var capturedValue = opt.Value;
            buttons.Add(new ChoiceButtonViewModel(
                opt.Text,
                opt.IsPrimary,
                () => onResult(capturedValue, vmRef?.IsChecked ?? false)));
        }

        if (cancelText != null)
        {
            buttons.Add(new ChoiceButtonViewModel(
                cancelText,
                isPrimary: false,
                () => onCancel?.Invoke()));
        }

        var vm = new ChoiceDialogViewModel(title, message, checkBoxText, buttons);
        vmRef = vm;
        return vm;
    }
}