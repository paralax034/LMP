using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using LMP.Core.Services;

namespace LMP.UI.Controls;

/// <summary>
/// Универсальная кнопка копирования ссылки.
/// Показывает hint-popup над кнопкой (аналогично RepeatHint/LikeHint в PlayerBar).
/// Визуально отражает состояние Success/Error через fade-смену иконки + popup.
///
/// <para><b>Idle-состояние задаётся статически в AXAML</b> (Data + Foreground через DynamicResource),
/// code-behind управляет только анимацией Success/Error и возвратом к idle.</para>
/// </summary>
public partial class CopyLinkButton : UserControl
{
    private CancellationTokenSource? _stateCts;
    private Button? _copyButton;
    private Popup? _hintPopup;
    private PathIcon? _hintIcon;
    private TextBlock? _hintText;

    private const int StateDurationMs = 1200;

    private const string SuccessIconKey = "Icon.Check";
    private const string ErrorIconKey = "Icon.Close";

    #region Styled Properties

    /// <summary>Определяет, нужно ли показывать кольцо при наведении (hover-ring).</summary>
    public static readonly StyledProperty<bool> ShowHoverRingProperty =
        AvaloniaProperty.Register<CopyLinkButton, bool>(nameof(ShowHoverRing), true);

    public bool ShowHoverRing
    {
        get => GetValue(ShowHoverRingProperty);
        set => SetValue(ShowHoverRingProperty, value);
    }

    /// <summary>URL для копирования.</summary>
    public static readonly StyledProperty<string?> CopyUrlProperty =
        AvaloniaProperty.Register<CopyLinkButton, string?>(nameof(CopyUrl));

    public string? CopyUrl
    {
        get => GetValue(CopyUrlProperty);
        set => SetValue(CopyUrlProperty, value);
    }

    /// <summary>Текст hint-popup при успешном копировании.</summary>
    public static readonly StyledProperty<string?> SuccessTextProperty =
        AvaloniaProperty.Register<CopyLinkButton, string?>(nameof(SuccessText));

    public string? SuccessText
    {
        get => GetValue(SuccessTextProperty);
        set => SetValue(SuccessTextProperty, value);
    }

    /// <summary>Текст ToolTip.</summary>
    public static readonly StyledProperty<string?> ToolTipTextProperty =
        AvaloniaProperty.Register<CopyLinkButton, string?>(nameof(ToolTipText));

    public string? ToolTipText
    {
        get => GetValue(ToolTipTextProperty);
        set => SetValue(ToolTipTextProperty, value);
    }

    /// <summary>Размер иконки.</summary>
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<CopyLinkButton, double>(nameof(IconSize), 16.0);

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    /// <summary>Размер круглой кнопки.</summary>
    public static readonly StyledProperty<double> ButtonSizeProperty =
        AvaloniaProperty.Register<CopyLinkButton, double>(nameof(ButtonSize), 36.0);

    public double ButtonSize
    {
        get => GetValue(ButtonSizeProperty);
        set => SetValue(ButtonSizeProperty, value);
    }

    #endregion

    public CopyLinkButton()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _copyButton = this.FindControl<Button>("CopyButton");
        _hintPopup = this.FindControl<Popup>("HintPopup");
        _hintIcon = this.FindControl<PathIcon>("HintIcon");
        _hintText = this.FindControl<TextBlock>("HintText");

        // Idle-состояние (Data + Foreground) задано статически в AXAML через
        // StaticResource / DynamicResource — code-behind не трогает его при attach.
        // ApplyIdleState вызывается только после завершения анимации (возврат к idle).
    }

    private async void OnClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(CopyUrl)) return;

        try
        {
            await Clipboard.SetTextAsync(CopyUrl);
            await AnimateStateAsync(success: true);
        }
        catch
        {
            await AnimateStateAsync(success: false);
        }
    }

    /// <summary>
    /// Показывает hint-popup над кнопкой, меняет иконку кнопки через fade,
    /// затем возвращает оба в исходное состояние.
    /// CTS отменяет предыдущую анимацию при быстрых повторных кликах.
    /// </summary>
    private async Task AnimateStateAsync(bool success)
    {
        _stateCts?.Cancel();
        _stateCts?.Dispose();
        var cts = new CancellationTokenSource();
        _stateCts = cts;

        try
        {
            ShowHint(success);

            if (_copyButton != null)
            {
                _copyButton.Classes.Set("error", !success);
                _copyButton.Classes.Set("success", success);
            }

            await Task.Delay(StateDurationMs, cts.Token);

            HideHint();
            ApplyIdleState();
        }
        catch (OperationCanceledException)
        {
            HideHint();
            ApplyIdleState();
        }
    }

    private void ShowHint(bool success)
    {
        if (_hintPopup is null || _hintIcon is null || _hintText is null) return;

        var app = Application.Current;
        var theme = app?.ActualThemeVariant;

        if (app != null && theme != null)
        {
            string iconKey = success ? SuccessIconKey : ErrorIconKey;
            string brushKey = success ? "AccentBrush" : "SystemErrorBrush";

            if (app.Resources.TryGetResource(iconKey, theme, out var geo) && geo is StreamGeometry sg)
                _hintIcon.Data = sg;

            if (app.Resources.TryGetResource(brushKey, theme, out var br) && br is IBrush brush)
                _hintIcon.Foreground = brush;
        }

        _hintText.Text = success
            ? ResolveSuccessText()
            : ResolveErrorText();

        _hintPopup.IsOpen = true;
    }

    private string ResolveSuccessText()
    {
        if (!string.IsNullOrEmpty(SuccessText) && !SuccessText.StartsWith('['))
            return SuccessText;

        var L = LocalizationService.Instance;
        var localized = L.Get("Common_Copied");
        if (!string.IsNullOrEmpty(localized) && !localized.StartsWith('['))
            return localized;

        return L.CurrentLanguageCode == "ru" ? "Скопировано!" : "Copied!";
    }

    private static string ResolveErrorText()
    {
        var L = LocalizationService.Instance;
        var localized = L.Get("Common_CopyFailed");
        if (!string.IsNullOrEmpty(localized) && !localized.StartsWith('['))
            return localized;

        return L.CurrentLanguageCode == "ru" ? "Ошибка копирования" : "Copy failed";
    }

    private void HideHint()
    {
        if (_hintPopup != null)
            _hintPopup.IsOpen = false;
    }

    /// <summary>
    /// Восстанавливает idle-иконку после завершения анимации.
    /// В отличие от AXAML-инициализации, здесь ресурсы гарантированно доступны
    /// (вызывается только внутри <see cref="AnimateStateAsync"/> после Task.Delay).
    /// Если TryGetResource недоступен — откатываемся к DynamicResource через ClearValue.
    /// </summary>
    private void ApplyIdleState()
    {
        if (_copyButton is null) return;

        _copyButton.Classes.Set("success", false);
        _copyButton.Classes.Set("error", false);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _stateCts?.Cancel();
        _stateCts?.Dispose();
        _stateCts = null;
        _copyButton = null;
        _hintPopup = null;
        _hintIcon = null;
        _hintText = null;
        base.OnUnloaded(e);
    }
}