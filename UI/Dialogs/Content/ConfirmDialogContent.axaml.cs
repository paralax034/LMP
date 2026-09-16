using Avalonia.Controls;

namespace LMP.UI.Dialogs.Content;

/// <summary>
/// Контент overlay-диалога подтверждения (Yes/No, OK/Cancel).
/// 
/// <para><b>Все свойства immutable</b> — задаются через конструктор,
/// DataContext устанавливается после инициализации полей,
/// поэтому XAML-биндинги корректно подхватывают значения.</para>
/// </summary>
public partial class ConfirmDialogContent : UserControl
{
    /// <summary>Заголовок диалога.</summary>
    public string Title { get; }

    /// <summary>Текст сообщения.</summary>
    public string Message { get; }

    /// <summary>Текст кнопки подтверждения.</summary>
    public string ConfirmText { get; }

    /// <summary>Текст кнопки отмены.</summary>
    public string CancelText { get; }

    /// <summary>Подтвердить — вызывает callback с <c>true</c>.</summary>
    public IRelayCommand ConfirmCommand { get; }

    /// <summary>Отменить — вызывает callback с <c>false</c>.</summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>
    /// Конструктор для XAML-компилятора и превьюера.
    /// Заполняет заглушками, чтобы дизайнер не падал.
    /// </summary>
    public ConfirmDialogContent()
        : this("Designer Title", "Designer Message text...", "OK", "Cancel", _ => { })
    {
    }

    /// <param name="title">Заголовок диалога.</param>
    /// <param name="message">Текст сообщения.</param>
    /// <param name="confirmText">Текст кнопки подтверждения.</param>
    /// <param name="cancelText">Текст кнопки отмены.</param>
    /// <param name="onResult">Callback: <c>true</c> = подтверждено, <c>false</c> = отменено.</param>
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