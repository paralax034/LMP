using Avalonia.Controls;

namespace LMP.UI.Dialogs.Content;

/// <summary>
/// Контент overlay-диалога с информационным сообщением (только кнопка OK).
/// </summary>
public partial class InfoDialogContent : UserControl
{
    /// <summary>Заголовок диалога.</summary>
    public string Title { get; }

    /// <summary>Текст сообщения.</summary>
    public string Message { get; }

    /// <summary>Текст кнопки закрытия.</summary>
    public string ButtonText { get; }

    /// <summary>Закрыть диалог.</summary>
    public IRelayCommand CloseCommand { get; }

    /// <summary>
    /// Конструктор для XAML-компилятора и превьюера.
    /// </summary>
    public InfoDialogContent()
        : this("Designer Title", "Info message for design time preview.", "OK", () => { })
    {
    }

    /// <param name="title">Заголовок диалога.</param>
    /// <param name="message">Текст сообщения.</param>
    /// <param name="buttonText">Текст кнопки закрытия.</param>
    /// <param name="onClose">Callback вызываемый при нажатии кнопки.</param>
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