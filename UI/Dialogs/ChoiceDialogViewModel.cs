namespace LMP.UI.Dialogs;

/// <summary>
/// Описание одной кнопки в диалоге выбора.
/// </summary>
/// <typeparam name="T">Тип значения, возвращаемого при нажатии.</typeparam>
public sealed class ChoiceOption<T>
{
    /// <summary>Текст кнопки.</summary>
    public required string Text { get; init; }

    /// <summary>Значение, возвращаемое при нажатии этой кнопки.</summary>
    public required T Value { get; init; }

    /// <summary>
    /// True — кнопка акцентная (primary). По умолчанию false.
    /// </summary>
    public bool IsPrimary { get; init; }
}

/// <summary>
/// ViewModel для одной кнопки в UI. Не generic — для работы с DataTemplate.
/// </summary>
public sealed class ChoiceButtonViewModel
{
    /// <summary>Текст кнопки.</summary>
    public string Text { get; }

    /// <summary>True — акцентная кнопка.</summary>
    public bool IsPrimary { get; }

    /// <summary>Команда нажатия.</summary>
    public IRelayCommand ClickCommand { get; }

    public ChoiceButtonViewModel(string text, bool isPrimary, Action onClick)
    {
        Text = text;
        IsPrimary = isPrimary;
        ClickCommand = new RelayCommand(onClick);
    }
}

/// <summary>
/// Универсальная ViewModel для overlay-диалога с произвольным набором кнопок
/// и опциональным чекбоксом.
/// 
/// <para><b>Использование:</b></para>
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
/// <para>Кнопка Cancel добавляется автоматически если указан <c>cancelText</c>.</para>
/// </summary>
public sealed partial class ChoiceDialogViewModel : ObservableObject
{
    /// <summary>Заголовок диалога.</summary>
    public string Title { get; }

    /// <summary>Текст сообщения (может быть null/empty — тогда не показывается).</summary>
    public string? Message { get; }

    /// <summary>Текст чекбокса. Null = чекбокс не показывается.</summary>
    public string? CheckBoxText { get; }

    /// <summary>Есть ли чекбокс.</summary>
    public bool HasCheckBox => !string.IsNullOrEmpty(CheckBoxText);

    /// <summary>Текущее состояние чекбокса.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>Список кнопок для отображения.</summary>
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
    /// Фабричный метод для создания типизированного диалога выбора.
    /// 
    /// <para>Генерирует <see cref="ChoiceDialogViewModel"/> (не generic)
    /// для совместимости с DataTemplate, но при этом callbacks типизированы.</para>
    /// </summary>
    /// <typeparam name="T">Тип значения кнопок.</typeparam>
    /// <param name="title">Заголовок.</param>
    /// <param name="message">Текст сообщения (опционально).</param>
    /// <param name="options">Список кнопок с их значениями.</param>
    /// <param name="onResult">
    /// Callback при нажатии любой кнопки (кроме Cancel).
    /// Параметры: (value, isCheckBoxChecked).
    /// </param>
    /// <param name="onCancel">Callback при отмене (опционально).</param>
    /// <param name="cancelText">
    /// Текст кнопки отмены. Если указан — кнопка Cancel добавляется автоматически.
    /// Если null — кнопки Cancel нет.
    /// </param>
    /// <param name="checkBoxText">Текст чекбокса. Null — чекбокс не показывается.</param>
    public static ChoiceDialogViewModel Create<T>(
        string title,
        string? message,
        IReadOnlyList<ChoiceOption<T>> options,
        Action<T, bool> onResult,
        Action? onCancel = null,
        string? cancelText = null,
        string? checkBoxText = null)
    {
        // Ссылка на будущую VM для доступа к IsChecked из кнопок
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