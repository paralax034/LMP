using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace LMP.UI.Controls;

/// <summary>
/// Universal link copy button.
/// Displays a hint popup above the button (similar to RepeatHint/LikeHint in PlayerBar).
/// Visually reflects the Success/Error state via icon fade transition + popup.
///
/// <para><b>Idle state is set statically in AXAML</b> (Data + Foreground via DynamicResource),
/// code-behind only controls Success/Error animations and returning to idle.</para>
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

    /// <summary>Determines whether to show the hover ring on pointer over.</summary>
    public static readonly StyledProperty<bool> ShowHoverRingProperty =
        AvaloniaProperty.Register<CopyLinkButton, bool>(nameof(ShowHoverRing), true);

    public bool ShowHoverRing
    {
        get => GetValue(ShowHoverRingProperty);
        set => SetValue(ShowHoverRingProperty, value);
    }

    /// <summary>URL to copy.</summary>
    public static readonly StyledProperty<string?> CopyUrlProperty =
        AvaloniaProperty.Register<CopyLinkButton, string?>(nameof(CopyUrl));

    public string? CopyUrl
    {
        get => GetValue(CopyUrlProperty);
        set => SetValue(CopyUrlProperty, value);
    }

    /// <summary>Hint popup text on successful copy.</summary>
    public static readonly StyledProperty<string?> SuccessTextProperty =
        AvaloniaProperty.Register<CopyLinkButton, string?>(nameof(SuccessText));

    public string? SuccessText
    {
        get => GetValue(SuccessTextProperty);
        set => SetValue(SuccessTextProperty, value);
    }

    /// <summary>ToolTip text.</summary>
    public static readonly StyledProperty<string?> ToolTipTextProperty =
        AvaloniaProperty.Register<CopyLinkButton, string?>(nameof(ToolTipText));

    public string? ToolTipText
    {
        get => GetValue(ToolTipTextProperty);
        set => SetValue(ToolTipTextProperty, value);
    }

    /// <summary>Icon size.</summary>
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<CopyLinkButton, double>(nameof(IconSize), 16.0);

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    /// <summary>Circular button size.</summary>
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

        // Idle state (Data + Foreground) is set statically in AXAML via
        // StaticResource / DynamicResource — code-behind does not touch it on attach.
        // ApplyIdleState is called only after the animation completes (return to idle).
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
    /// Displays a hint popup above the button, fades the button icon,
    /// then restores both to their original state.
    /// CTS cancels the previous animation on rapid consecutive clicks.
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

            if (!await cts.Token.DelayNoThrowAsync(StateDurationMs))
                return;

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
        if (!string.IsNullOrEmpty(SuccessText))
            return SuccessText;

        return LocalizationService.Instance["Common_Copied"];
    }

    private static string ResolveErrorText()
    {
        return LocalizationService.Instance["Common_CopyFailed"];
    }

    private void HideHint()
    {
        _hintPopup?.IsOpen = false;
    }

    /// <summary>
    /// Restores the idle icon after animation completes.
    /// Unlike AXAML initialization, resources are guaranteed to be available here
    /// (called only inside <see cref="AnimateStateAsync"/> after Task.Delay).
    /// If TryGetResource is unavailable, falls back to DynamicResource via ClearValue.
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