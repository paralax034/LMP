using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace LMP.UI.Features.Shell;

/// <summary>
/// Custom tray context menu popup window.
/// </summary>
public partial class TrayPopupWindow : Window
{
    #region Fields

    private PlayerControlService? _playerControl;
    private Action? _onShowWindow;
    private Action? _onExit;
    private Action? _onOpenQueue;
    private Action? _onCleanMemory;
    private Action<int>? _onVolumeChanged;

    /// <summary>
    /// Last known visibility state of the main window.
    /// Updated via <see cref="UpdateState(bool)"/> before each popup display.
    /// Used for correct Show/Hide button text/icon.
    /// </summary>
    private bool _lastKnownWindowVisible;

    /// <summary>
    /// Flag indicating whether player service event subscriptions are active.
    /// </summary>
    private bool _isSubscribed;

    // UI controls
    private Border? _trackInfoSection;
    private TextBlock? _trackTitleText;
    private TextBlock? _trackAuthorText;
    private Button? _showButton;
    private TextBlock? _showText;
    private PathIcon? _showIcon;
    private Button? _playPauseButton;
    private PathIcon? _playPauseIcon;
    private TextBlock? _playPauseText;
    private Button? _nextButton;
    private TextBlock? _nextText;
    private Button? _prevButton;
    private TextBlock? _prevText;
    private Button? _repeatButton;
    private PathIcon? _repeatIcon;
    private TextBlock? _repeatText;
    private Border? _volumeSection;
    private PathIcon? _volumeIcon;
    private TextBlock? _volumeText;
    private Button? _queueButton;
    private TextBlock? _queueText;
    private Button? _cleanMemButton;
    private TextBlock? _cleanMemText;
    private Button? _exitButton;
    private TextBlock? _exitText;

    /// <summary>Volume step when scrolling in popup.</summary>
    private const int VolumeStep = 2;

    /// <summary>Exact fixed width of the popup for position calculation (DIP).</summary>
    private const int PopupWidth = 260;

    /// <summary>Approximate height of the popup for position calculation (DIP).</summary>
    private const int EstimatedHeight = 390;

    #endregion

    #region Constructor & Init

    /// <summary>
    /// Public parameterless constructor — required for Avalonia XAML loader (AVLN3001).
    /// Not used directly — call <see cref="Initialize"/> after instantiation.
    /// </summary>
    public TrayPopupWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Initializes the popup with dependencies and subscriptions.
    /// Called once after instantiation via parameterless constructor.
    /// </summary>
    /// <param name="playerControl">Player control service</param>
    /// <param name="onShowWindow">Toggle show/hide main window</param>
    /// <param name="onExit">Exit application</param>
    /// <param name="onOpenQueue">Open queue</param>
    /// <param name="onCleanMemory">Clean memory</param>
    /// <param name="onVolumeChanged">Callback on volume change via scroll</param>
    public void Initialize(
        PlayerControlService playerControl,
        Action? onShowWindow,
        Action? onExit,
        Action? onOpenQueue,
        Action? onCleanMemory,
        Action<int>? onVolumeChanged = null)
    {
        _playerControl = playerControl;
        _onShowWindow = onShowWindow;
        _onExit = onExit;
        _onOpenQueue = onOpenQueue;
        _onCleanMemory = onCleanMemory;
        _onVolumeChanged = onVolumeChanged;

        ResolveControls();
        BindClickHandlers();

        // Volume scroll on volume section
        _volumeSection?.PointerWheelChanged += OnVolumeScroll;

        // Light-dismiss: close popup on focus loss
        Deactivated += (_, _) => HidePopup();
    }

    /// <summary>
    /// Retrieves references to all named controls from the XAML tree.
    /// Called once from <see cref="Initialize"/>.
    /// </summary>
    private void ResolveControls()
    {
        _trackInfoSection = this.FindControl<Border>("TrackInfoSection");
        _trackTitleText = this.FindControl<TextBlock>("TrackTitleText");
        _trackAuthorText = this.FindControl<TextBlock>("TrackAuthorText");
        _showButton = this.FindControl<Button>("ShowButton");
        _showText = this.FindControl<TextBlock>("ShowText");
        _showIcon = this.FindControl<PathIcon>("ShowIcon");
        _playPauseButton = this.FindControl<Button>("PlayPauseButton");
        _playPauseIcon = this.FindControl<PathIcon>("PlayPauseIcon");
        _playPauseText = this.FindControl<TextBlock>("PlayPauseText");
        _nextButton = this.FindControl<Button>("NextButton");
        _nextText = this.FindControl<TextBlock>("NextText");
        _prevButton = this.FindControl<Button>("PrevButton");
        _prevText = this.FindControl<TextBlock>("PrevText");
        _repeatButton = this.FindControl<Button>("RepeatButton");
        _repeatIcon = this.FindControl<PathIcon>("RepeatIcon");
        _repeatText = this.FindControl<TextBlock>("RepeatText");
        _volumeSection = this.FindControl<Border>("VolumeSection");
        _volumeIcon = this.FindControl<PathIcon>("VolumeIcon");
        _volumeText = this.FindControl<TextBlock>("VolumeText");
        _queueButton = this.FindControl<Button>("QueueButton");
        _queueText = this.FindControl<TextBlock>("QueueText");
        _cleanMemButton = this.FindControl<Button>("CleanMemButton");
        _cleanMemText = this.FindControl<TextBlock>("CleanMemText");
        _exitButton = this.FindControl<Button>("ExitButton");
        _exitText = this.FindControl<TextBlock>("ExitText");
    }

    /// <summary>
    /// Binds click handlers to all menu buttons.
    /// Called once from <see cref="Initialize"/>.
    /// </summary>
    private void BindClickHandlers()
    {
        _showButton?.Click += OnShowClick;
        _playPauseButton?.Click += OnPlayPauseClick;
        _nextButton?.Click += OnNextClick;
        _prevButton?.Click += OnPrevClick;
        _repeatButton?.Click += OnRepeatClick;
        _queueButton?.Click += OnQueueClick;
        _cleanMemButton?.Click += OnCleanMemClick;
        _exitButton?.Click += OnExitClick;
    }

    #endregion

    #region State Update

    /// <summary>
    /// Updates the state of all popup elements before showing.
    /// </summary>
    /// <param name="isWindowVisible">
    /// <c>true</c> — main window is visible (button shows "Hide").
    /// <c>false</c> — window is hidden/in tray (button shows "Show").
    /// </param>
    public void UpdateState(bool isWindowVisible = false)
    {
        if (_playerControl == null) return;

        _lastKnownWindowVisible = isWindowVisible;

        var L = LocalizationService.Instance;
        var track = _playerControl.CurrentTrack;
        bool hasTrack = track != null;

        // Track info
        UpdateTrackInfo(track, hasTrack);

        // Show / Hide — text and icon depend on window state
        UpdateShowHideButton(isWindowVisible, L);

        // Play/Pause
        UpdatePlayPauseButton(_playerControl.IsPlaying, L);

        // Next / Previous
        SetText(_nextText, L["Tray_Next"]);
        SetText(_prevText, L["Tray_Previous"]);

        // Repeat
        UpdateRepeatButton(_playerControl.RepeatMode, L);

        // Volume
        UpdateVolumeDisplay();

        if (_volumeSection != null)
            ToolTip.SetTip(_volumeSection, L["Tray_VolumeScrollHint"]);

        // Enabled states (playback controls)
        SetEnabled(_playPauseButton, hasTrack);
        SetEnabled(_nextButton, hasTrack);
        SetEnabled(_prevButton, hasTrack);
        SetEnabled(_repeatButton, hasTrack);

        // Bottom items
        SetText(_queueText, L["Tray_Queue"]);
        SetText(_cleanMemText, L["Tray_ClearMemory"]);
        SetText(_exitText, L["Tray_Exit"]);
    }

    /// <summary>
    /// Updates track info section.
    /// </summary>
    private void UpdateTrackInfo(TrackInfo? track, bool hasTrack)
    {
        _trackInfoSection?.IsVisible = hasTrack;

        if (hasTrack && track != null)
        {
            SetText(_trackTitleText, track.Title);
            SetText(_trackAuthorText, track.Author);
        }
    }

    /// <summary>
    /// Updates the Show/Hide button: text and icon based on window visibility.
    /// </summary>
    private void UpdateShowHideButton(bool isWindowVisible, LocalizationService L)
    {
        if (isWindowVisible)
        {
            SetText(_showText, L["Tray_Hide"]);
            SetIcon(_showIcon, "Icon.ChevronDown");
        }
        else
        {
            SetText(_showText, L["Tray_Show"]);
            SetIcon(_showIcon, "Icon.Home");
        }
    }

    /// <summary>
    /// Updates the Play/Pause button: text and icon based on playback state.
    /// </summary>
    private void UpdatePlayPauseButton(bool isPlaying, LocalizationService L)
    {
        if (isPlaying)
        {
            SetText(_playPauseText, L["Tray_Pause"]);
            SetIcon(_playPauseIcon, "Icon.Pause");
        }
        else
        {
            SetText(_playPauseText, L["Tray_Play"]);
            SetIcon(_playPauseIcon, "Icon.Play");
        }
    }

    /// <summary>
    /// Updates the Repeat button: text and icon based on repeat mode.
    /// </summary>
    private void UpdateRepeatButton(RepeatMode repeatMode, LocalizationService L)
    {
        if (_repeatText == null || _repeatIcon == null) return;

        var (text, iconKey) = repeatMode switch
        {
            RepeatMode.All => (L["Tray_RepeatAll"], "Icon.Repeat"),
            RepeatMode.One => (L["Tray_RepeatOne"], "Icon.RepeatOnce"),
            _ => (L["Tray_Repeat"], "Icon.Repeat")
        };

        _repeatText.Text = text;
        SetIcon(_repeatIcon, iconKey);
    }

    /// <summary>
    /// Updates volume display (icon + text).
    /// </summary>
    private void UpdateVolumeDisplay()
    {
        if (_playerControl == null || _volumeText == null || _volumeIcon == null) return;

        var volume = _playerControl.GetCurrentVolume();
        _volumeText.Text = $"{volume}%";

        var iconKey = volume switch
        {
            0 => "Icon.VolumeMute",
            <= 33 => "Icon.VolumeLow",
            <= 66 => "Icon.VolumeMedium",
            _ => "Icon.VolumeHigh"
        };

        SetIcon(_volumeIcon, iconKey);
    }

    #endregion

    #region Subscriptions

    /// <summary>
    /// Subscribes to player state change events.
    /// Active only while the popup is visible.
    /// </summary>
    private void SubscribeToPlayerChanges()
    {
        if (_isSubscribed || _playerControl == null) return;
        _isSubscribed = true;

        _playerControl.IsPlayingChanged += OnIsPlayingChanged;
        _playerControl.CurrentTrackChanged += OnCurrentTrackChanged;
        _playerControl.RepeatModeChanged += OnRepeatModeChanged;
        _playerControl.VolumeChanged += OnVolumeChanged;
    }

    /// <summary>
    /// Unsubscribes from player state changes to prevent memory leaks 
    /// and avoid unnecessary CPU load when the window is hidden.
    /// </summary>
    private void UnsubscribeFromPlayerChanges()
    {
        if (!_isSubscribed || _playerControl == null) return;
        _isSubscribed = false;

        _playerControl.IsPlayingChanged -= OnIsPlayingChanged;
        _playerControl.CurrentTrackChanged -= OnCurrentTrackChanged;
        _playerControl.RepeatModeChanged -= OnRepeatModeChanged;
        _playerControl.VolumeChanged -= OnVolumeChanged;
    }

    private void OnIsPlayingChanged(bool isPlaying)
    {
        Dispatcher.UIThread.Post(() => UpdatePlayPauseButton(isPlaying, LocalizationService.Instance));
    }

    private void OnCurrentTrackChanged(TrackInfo? track)
    {
        Dispatcher.UIThread.Post(() =>
        {
            bool hasTrack = track != null;
            UpdateTrackInfo(track, hasTrack);

            SetEnabled(_playPauseButton, hasTrack);
            SetEnabled(_nextButton, hasTrack);
            SetEnabled(_prevButton, hasTrack);
            SetEnabled(_repeatButton, hasTrack);
        });
    }

    private void OnRepeatModeChanged(RepeatMode mode)
    {
        Dispatcher.UIThread.Post(() => UpdateRepeatButton(mode, LocalizationService.Instance));
    }

    private void OnVolumeChanged(int volume)
    {
        Dispatcher.UIThread.Post(UpdateVolumeDisplay);
    }

    #endregion

    #region Show / Position

    /// <summary>
    /// Displays the popup near the cursor, adjusting position for the screen work area.
    /// </summary>
    /// <param name="x">Cursor X coordinate in screen pixels</param>
    /// <param name="y">Cursor Y coordinate in screen pixels</param>
    /// <param name="isWindowVisible">Current main window visibility state. If null, the last cached value is used.</param>
    public void ShowAt(int x, int y, bool? isWindowVisible = null)
    {
        UpdateState(isWindowVisible ?? _lastKnownWindowVisible);

        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen != null)
        {
            var workArea = screen.WorkingArea;
            var scaling = screen.Scaling;

            double dipX = x / scaling;
            double dipY = y / scaling;
            double workRight = workArea.Right / scaling;
            double workBottom = workArea.Bottom / scaling;
            double workLeft = workArea.X / scaling;
            double workTop = workArea.Y / scaling;

            // Adjust position so the fixed-width window does not exceed monitor bounds
            if (dipX + PopupWidth > workRight)
                dipX = workRight - PopupWidth;

            if (dipY + EstimatedHeight > workBottom)
                dipY -= EstimatedHeight;

            dipX = Math.Clamp(dipX, workLeft, Math.Max(workLeft, workRight - PopupWidth));
            dipY = Math.Clamp(dipY, workTop, Math.Max(workTop, workBottom - EstimatedHeight));

            Position = new PixelPoint((int)(dipX * scaling), (int)(dipY * scaling));
        }
        else
        {
            Position = new PixelPoint(x, y);
        }

        Show();
        Activate();

        // Activate subscriptions upon actual window opening
        SubscribeToPlayerChanges();
    }

    /// <summary>Hides the popup (light-dismiss).</summary>
    private void HidePopup()
    {
        // Unsubscribe from events immediately upon hiding the window to save resources
        UnsubscribeFromPlayerChanges();
        Hide();
    }

    #endregion

    #region Click Handlers

    private void OnShowClick(object? sender, RoutedEventArgs e)
    {
        HidePopup();
        _onShowWindow?.Invoke();
    }

    private async void OnPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        if (_playerControl == null) return;
        await _playerControl.PlayPauseAsync();
    }

    private async void OnNextClick(object? sender, RoutedEventArgs e)
    {
        HidePopup();
        if (_playerControl != null) await _playerControl.NextAsync();
    }

    private async void OnPrevClick(object? sender, RoutedEventArgs e)
    {
        HidePopup();
        if (_playerControl != null) await _playerControl.PreviousAsync();
    }

    private void OnRepeatClick(object? sender, RoutedEventArgs e)
    {
        _playerControl?.ToggleRepeat();
    }

    private void OnQueueClick(object? sender, RoutedEventArgs e)
    {
        HidePopup();
        _onOpenQueue?.Invoke();
    }

    private void OnCleanMemClick(object? sender, RoutedEventArgs e)
    {
        HidePopup();
        _onCleanMemory?.Invoke();
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        HidePopup();
        _onExit?.Invoke();
    }

    #endregion

    #region Volume Scroll

    /// <summary>
    /// Mouse wheel scroll on volume section — changes volume by ±<see cref="VolumeStep"/>.
    /// </summary>
    private void OnVolumeScroll(object? sender, PointerWheelEventArgs e)
    {
        if (_playerControl == null) return;

        int delta = e.Delta.Y > 0 ? VolumeStep : -VolumeStep;
        int newVolume = _playerControl.AdjustVolume(delta);

        if (!_isSubscribed)
            UpdateVolumeDisplay();

        _onVolumeChanged?.Invoke(newVolume);

        e.Handled = true;
    }

    #endregion

    #region Keyboard

    /// <summary>
    /// Escape → close popup.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePopup();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    #endregion

    #region Window Lifecycle

    /// <summary>
    /// Guarantees unsubscription from all events upon physical window closing/destruction.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        UnsubscribeFromPlayerChanges();
        base.OnClosed(e);
    }

    #endregion

    #region Helpers — Null-safe Property Setters

    /// <summary>
    /// Null-safe Data assignment for PathIcon via StreamGeometry named resource.
    /// </summary>
    /// <param name="control">PathIcon control</param>
    /// <param name="resourceKey">Key from Icons.axaml</param>
    private static void SetIcon(PathIcon? control, string resourceKey)
    {
        if (control == null) return;

        if (Application.Current?.Resources.TryGetResource(
                resourceKey,
                Application.Current.ActualThemeVariant,
                out var res) == true
            && res is StreamGeometry geometry)
        {
            control.Data = geometry;
        }
    }

    /// <summary>
    /// Null-safe Text assignment for TextBlock.
    /// </summary>
    private static void SetText(TextBlock? control, string? text)
    {
        control?.Text = text;
    }

    /// <summary>
    /// Null-safe IsEnabled assignment for Control.
    /// </summary>
    private static void SetEnabled(Control? control, bool enabled)
    {
        control?.IsEnabled = enabled;
    }

    #endregion
}