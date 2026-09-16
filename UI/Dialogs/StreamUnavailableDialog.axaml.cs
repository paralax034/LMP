using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LMP.Core.Youtube.Exceptions;

namespace LMP.UI.Dialogs;

public partial class StreamUnavailableDialog : Window
{
    private static readonly LocalizationService L = LocalizationService.Instance;

    private string _fullErrorDetails = "";

    #region Styled Properties

    public static readonly StyledProperty<string> DialogTitleProperty =
        AvaloniaProperty.Register<StreamUnavailableDialog, string>(nameof(DialogTitle), "");

    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<StreamUnavailableDialog, string>(nameof(Message), "");

    public static readonly StyledProperty<string> TechnicalInfoProperty =
        AvaloniaProperty.Register<StreamUnavailableDialog, string>(nameof(TechnicalInfo), "");

    public static readonly StyledProperty<bool> HasTechnicalInfoProperty =
        AvaloniaProperty.Register<StreamUnavailableDialog, bool>(nameof(HasTechnicalInfo), false);

    public static readonly StyledProperty<string> CloseButtonTextProperty =
        AvaloniaProperty.Register<StreamUnavailableDialog, string>(nameof(CloseButtonText), "OK");

    public static readonly StyledProperty<string> CopyErrorTextProperty =
        AvaloniaProperty.Register<StreamUnavailableDialog, string>(nameof(CopyErrorText), "Copy Error");

    public static readonly StyledProperty<bool> ShowCopyButtonProperty =
        AvaloniaProperty.Register<StreamUnavailableDialog, bool>(nameof(ShowCopyButton), true);

    #endregion

    #region Properties

    public string DialogTitle
    {
        get => GetValue(DialogTitleProperty);
        set => SetValue(DialogTitleProperty, value);
    }

    public string Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string TechnicalInfo
    {
        get => GetValue(TechnicalInfoProperty);
        set
        {
            SetValue(TechnicalInfoProperty, value);
            HasTechnicalInfo = !string.IsNullOrEmpty(value);
        }
    }

    public bool HasTechnicalInfo
    {
        get => GetValue(HasTechnicalInfoProperty);
        set => SetValue(HasTechnicalInfoProperty, value);
    }

    public string CloseButtonText
    {
        get => GetValue(CloseButtonTextProperty);
        set => SetValue(CloseButtonTextProperty, value);
    }

    public string CopyErrorText
    {
        get => GetValue(CopyErrorTextProperty);
        set => SetValue(CopyErrorTextProperty, value);
    }

    public bool ShowCopyButton
    {
        get => GetValue(ShowCopyButtonProperty);
        set => SetValue(ShowCopyButtonProperty, value);
    }

    public IRelayCommand CloseCommand { get; }
    public IAsyncRelayCommand CopyErrorCommand { get; }

    #endregion

    public StreamUnavailableDialog()
    {
        InitializeComponent();

        // Локализация
        DialogTitle = L["Dialog_Error_Title"];
        CloseButtonText = L["Common_OK"];
        CopyErrorText = L["Error_CopyDetails"];

        CloseCommand = new RelayCommand(() =>
        {
            if (IsLoaded) Close();
        });

        CopyErrorCommand = new AsyncRelayCommand(CopyErrorToClipboardAsync);

        DataContext = this;
    }

    /// <summary>
    /// Настраивает диалог для StreamUnavailableException.
    /// </summary>
    public void ConfigureForException(StreamUnavailableException exception)
    {
        // Получаем локализованное сообщение по ключу
        var locKey = exception.GetLocalizationKey();
        Message = L.Get(locKey, GetFallbackMessage(exception));

        // Техническая информация
        var techInfo = new System.Text.StringBuilder();
        techInfo.AppendLine($"Video ID: {exception.VideoId}");
        techInfo.AppendLine($"Reason: {exception.Reason}");

        if (exception.HttpStatusCode.HasValue)
        {
            techInfo.AppendLine($"HTTP Status: {exception.HttpStatusCode}");
        }

        if (exception.WasHlsFallback)
        {
            techInfo.AppendLine("HLS Fallback: Yes");
        }

        TechnicalInfo = techInfo.ToString().TrimEnd();

        // Полная информация для копирования
        _fullErrorDetails = BuildFullErrorDetails(exception);

        ShowCopyButton = true;
    }

    /// <summary>
    /// Настраивает диалог для общей ошибки.
    /// </summary>
    public void ConfigureForError(string videoId, string errorMessage, Exception? exception = null)
    {
        Message = L["Error_Stream_Generic"];

        var techInfo = new System.Text.StringBuilder();
        techInfo.AppendLine($"Video ID: {videoId}");
        techInfo.AppendLine($"Error: {errorMessage}");

        TechnicalInfo = techInfo.ToString().TrimEnd();

        _fullErrorDetails = $"""
            === LMP Playback Error ===
            Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
            Video ID: {videoId}
            Error: {errorMessage}
            
            Exception Type: {exception?.GetType().Name ?? "N/A"}
            Stack Trace:
            {exception?.StackTrace ?? "N/A"}
            """;

        ShowCopyButton = true;
    }

    private static string GetFallbackMessage(StreamUnavailableException ex)
    {
        // Fallback на английском если ключ не найден
        return ex.Reason switch
        {
            StreamUnavailableReason.Forbidden403 when ex.WasHlsFallback
                => "HLS stream blocked (403). Please contact the developer.",

            StreamUnavailableReason.Forbidden403
                => "Track access forbidden (403). Please contact the developer.",

            StreamUnavailableReason.AllClientsFailed
                => "Could not access track. Please contact the developer.",

            StreamUnavailableReason.RegionBlocked
                => "Track not available in your region.",

            StreamUnavailableReason.AgeRestricted
                => "Track is age-restricted. Please sign in.",

            StreamUnavailableReason.LiveStream
                => "Live streams are not supported.",

            StreamUnavailableReason.Private
                => "This is a private video.",

            StreamUnavailableReason.Removed
                => "Video has been removed.",

            _ => "Track unavailable. Please contact the developer."
        };
    }

    private static string BuildFullErrorDetails(StreamUnavailableException ex)
    {
        return $"""
            === LMP Stream Error ===
            Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
            Video ID: {ex.VideoId}
            Reason: {ex.Reason}
            HTTP Status: {ex.HttpStatusCode?.ToString() ?? "N/A"}
            HLS Fallback: {ex.WasHlsFallback}
            Message: {ex.Message}
            
            Stack Trace:
            {ex.StackTrace}
            """;
    }

    private async Task CopyErrorToClipboardAsync()
    {
        try
        {
            await Helpers.Clipboard.SetTextAsync(_fullErrorDetails);

            // Временно меняем текст кнопки
            var originalText = CopyErrorText;
            CopyErrorText = L["Track_Copied"];

            await Task.Delay(1500);
            CopyErrorText = originalText;
        }
        catch (Exception ex)
        {
            Log.Warn($"[StreamUnavailableDialog] Copy failed: {ex.Message}");
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}