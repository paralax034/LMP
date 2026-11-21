using LMP.Core.Audio.Normalization;

namespace LMP.Core.Models;

public enum YoutubeClientProfile
{
    WebRemix,    // n + sig (All videos work)
    AndroidVR,   // Most videos work
    Web,         // n + sig
    TV,          // Not working
    Ios,         // Mostly HLS
    AndroidMusic // Requires additional actions
}

public enum InternetProfile
{
    Low,    // Traffic saving / Slow internet
    Medium, // Balanced (default)
    High,   // High quality / Fast internet
    Ultra   // Maximum caching / Local network
}

/// <summary>
/// Batching (coalescence) interval for cloud mutations before sending to YouTube.
/// The enum value corresponds to the window duration in seconds (from 10 seconds to 3 hours).
/// </summary>
public enum CloudSyncBatchInterval
{
    /// <summary>10 seconds (fast click series coalescence).</summary>
    Sec10 = 10,

    /// <summary>30 seconds.</summary>
    Sec30 = 30,

    /// <summary>1 minute (default — balance between responsiveness and request count).</summary>
    Min1 = 60,

    /// <summary>5 minutes.</summary>
    Min5 = 300,

    /// <summary>15 minutes.</summary>
    Min15 = 900,

    /// <summary>30 minutes.</summary>
    Min30 = 1800,

    /// <summary>1 hour.</summary>
    Hour1 = 3600,

    /// <summary>3 hours.</summary>
    Hour3 = 10800
}

/// <summary>
/// Volume interpolation curve type.
/// </summary>
public enum VolumeCurveType
{
    /// <summary>Linear: volume = t</summary>
    Linear,

    /// <summary>Quadratic: volume = t² (default, perceptually linear)</summary>
    Quadratic,

    /// <summary>Logarithmic: volume = log2(1 + t) / log2(2)</summary>
    Logarithmic,

    /// <summary>Cubic: volume = t³</summary>
    Cubic,

    /// <summary>
    /// "Speed of light": exponential growth at the end.
    /// Formula: volume = (e^(t*2) - 1) / (e² - 1)
    /// </summary>
    SpeedOfLight
}

/// <summary>
/// Animation speed of the active playing track in the track list.
/// </summary>
public enum TrackAnimationSpeed
{
    /// <summary>Very slow smooth animation.</summary>
    VerySlow,

    /// <summary>Slow smooth animation.</summary>
    Slow,

    /// <summary>Balanced standard speed (default).</summary>
    Medium,

    /// <summary>Fast dynamic animation.</summary>
    Fast,

    /// <summary>Extremely fast high-frequency animation.</summary>
    Epileptic
}

/// <summary>
/// Visual style of the frequency-reactive ambient backdrop in Queue and Playlist views.
/// </summary>
public enum AmbientBackdropMode
{
    /// <summary>Disabled (0% rendering overhead).</summary>
    Off = 0,

    /// <summary>Organic breathing aurora curtain with central vocal glow and radial shockwaves.</summary>
    Aurora = 1,

    /// <summary>Traveling left-to-right audio-reactive fluid wave matching the active track border (default).</summary>
    FluidWave = 2
}

/// <summary>
/// Critical playback failure handling strategy.
/// </summary>
/// <remarks>
/// <para>Determines whether manual user action is required or if recovery can be automatic.</para>
/// <para>If automatic mode is selected, the specific queue action is defined by <see cref="PlaybackFailureBehavior"/>.</para>
/// <para>Used in <see cref="UI.Services.PlaybackErrorOrchestrator"/>.</para>
/// </remarks>
public enum PlaybackErrorBehavior
{
    /// <summary>
    /// Stop automatic recovery and wait for explicit user action.
    /// If there is a next track in the queue, playback pauses at the current state.
    /// If it is the last track, the active track finishes with a complete stop.
    /// </summary>
    Dialog,

    /// <summary>
    /// Recover automatically and display an error notification.
    /// The specific action after an error is defined by <see cref="PlaybackFailureBehavior"/>.
    /// </summary>
    ToastAndSkip,

    /// <summary>
    /// Recover automatically without displaying a notification.
    /// The specific action after an error is defined by <see cref="PlaybackFailureBehavior"/>.
    /// </summary>
    Ignore
}

/// <summary>
/// Notification display mode during complex n-token deciphering.
/// </summary>
public enum NTokenNotificationMode
{
    /// <summary>
    /// Do not create a notification at all.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Show a popup toast notification and save the entry to the notification center.
    /// </summary>
    Toast = 1,

    /// <summary>
    /// Save the entry only to the notification center without showing a toast.
    /// </summary>
    PanelOnly = 2
}

/// <summary>
/// Behavior for automatic recovery after a critical playback error.
/// </summary>
/// <remarks>
/// Applies only if <see cref="AudioSettings.CriticalErrorBehavior"/> allows automatic recovery.
/// </remarks>
public enum PlaybackFailureBehavior
{
    /// <summary>
    /// Automatically switch to the next track and continue playback.
    /// </summary>
    SkipAndPlay,

    /// <summary>
    /// Automatically switch to the next track and pause playback.
    /// </summary>
    SkipAndPause,

    /// <summary>
    /// Exit the current track and completely stop playback without advancing to the next track.
    /// </summary>
    Stop
}

public enum RepeatMode
{
    None,
    One,
    All
}

public enum AudioQualityPreference
{
    BestAvailable,
    Standard
}

/// <summary>
/// Discord Rich Presence activity verb type sent in <c>SET_ACTIVITY</c>.
/// Numeric values match the official Discord RPC ActivityType enum.
/// </summary>
public enum DiscordRpcActivityType
{
    /// <summary>"Listening to ..." — displays Spotify-style music progress bar.</summary>
    Listening = 2,

    /// <summary>"Playing ..."</summary>
    Playing = 0,

    /// <summary>"Watching ..."</summary>
    Watching = 3
}

/// <summary>
/// Controls which field Discord displays in the compact member-list status text (<c>status_display_type</c>).
/// </summary>
public enum DiscordRpcStatusDisplay
{
    /// <summary>Displays application name (e.g. "Listening to Lite Music Player").</summary>
    AppName = 0,

    /// <summary>Displays the <c>state</c> field (artist name).</summary>
    Artist = 1,

    /// <summary>Displays the <c>details</c> field (track title).</summary>
    TrackTitle = 2
}

/// <summary>
/// Controls how playback progress timestamps are rendered in Discord Rich Presence.
/// </summary>
public enum DiscordRpcTimestampMode
{
    /// <summary>Sends both start and end timestamps so Discord renders a live progress bar.</summary>
    ProgressBar = 0,

    /// <summary>Sends only the start timestamp (elapsed timer).</summary>
    ElapsedOnly = 1,

    /// <summary>Omits timestamps completely.</summary>
    None = 2
}

/// <summary>
/// Formatting mode for the primary Rich Presence title line (<c>details</c>).
/// </summary>
public enum DiscordRpcDetailsFormat
{
    /// <summary>Track title only (artist shown on the second line).</summary>
    TrackTitle = 0,

    /// <summary>"Title — Artist".</summary>
    TrackAndArtist = 1,

    /// <summary>"Artist — Title".</summary>
    ArtistAndTrack = 2
}

/// <summary>
/// Controls Discord Rich Presence behavior when playback is paused.
/// </summary>
public enum DiscordRpcPausedBehavior
{
    /// <summary>Keeps the track visible without a ticking progress bar and shows a paused state.</summary>
    ShowPaused = 0,

    /// <summary>Clears the Discord activity while paused.</summary>
    ClearPresence = 1
}

/// <summary>
/// Style of the small corner badge (<c>assets.small_image</c>) on the Discord Rich Presence cover.
/// </summary>
public enum DiscordRpcBadgeStyle
{
    /// <summary>No corner badge — cleanest minimalist cover.</summary>
    None = 0,

    /// <summary>Shows the public YouTube playlist cover in the corner with a clickable playlist link.</summary>
    PublicPlaylistCover = 1,

    /// <summary>Shows the authenticated listener's YouTube channel avatar with a clickable channel link.</summary>
    ListenerAvatar = 2,

    /// <summary>User-supplied external HTTPS image, GIF, or animated WebP URL.</summary>
    Custom = 3
}

/// <summary>
/// One-click layout presets for Discord Rich Presence appearance.
/// </summary>
public enum DiscordRpcPreset
{
    Minimal = 0,
    Balanced = 1,
    Rich = 2
}

public sealed class ProxySettings
{
    public bool Enabled { get; set; } = false;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8080;
    public bool UseAuth { get; set; } = false;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

/// <summary>
/// Data storage settings.
/// </summary>
public sealed class StorageSettings
{
    /// <summary>
    /// Image cache limit in MB.
    /// </summary>
    public int ImageCacheLimitMb { get; set; } = 500;

    /// <summary>
    /// Audio cache (StreamCache) limit in MB.
    /// Old files are automatically purged when exceeded.
    /// </summary>
    public int AudioCacheLimitMb { get; set; } = 2048;

    public int DownloadedTracksLimitMb { get; set; } = 5000;

    /// <summary>
    /// Maximum number of images in RAM cache.
    /// </summary>
    public int MaxBitmapCacheItems { get; set; } = 25;

    /// <summary>
    /// Automatically save fully cached tracks to the Downloads folder.
    /// Disabled by default to save disk space (prevents file duplication).
    /// </summary>
    public bool AutoSaveToDownloads { get; set; } = false;
}

/// <summary>
/// Audio system settings.
/// </summary>
public sealed class AudioSettings
{
    /// <summary>
    /// Enable volume boost above 100%.
    /// If false, MaxVolume simply increases granularity (more steps).
    /// </summary>
    public bool VolumeBoostEnabled { get; set; } = true;

    /// <summary>
    /// Volume interpolation curve.
    /// </summary>
    public VolumeCurveType VolumeCurve { get; set; } = VolumeCurveType.Quadratic;

    /// <summary>
    /// Volume normalization (static level matching, similar to Spotify/YouTube Music).
    /// </summary>
    public bool NormalizationEnabled { get; set; } = true;

    /// <summary>
    /// Target normalization level in LUFS.
    /// Range: -24 (quieter) .. -6 (louder). Default is -14 (Spotify/YouTube Music standard).
    /// </summary>
    public float NormalizationTargetLufs { get; set; } = -14f;

    /// <summary>
    /// Maximum normalization gain. Limits amplification for quiet tracks.
    /// Range: 1.0 (no gain) .. 6.0. Default is 3.0.
    /// </summary>
    public float NormalizationMaxGain { get; set; } = 3.0f;

    /// <summary>
    /// Volume normalization mode.
    /// <para><b>Bidirectional</b> (default): boosts quiet tracks and reduces loud tracks.
    /// Ensures consistent loudness across all content.</para>
    /// <para><b>DownwardOnly</b>: reduces loud tracks only, similar to YouTube.
    /// Quiet tracks play at their original level.</para>
    /// </summary>
    public NormalizationMode NormalizationMode { get; set; } = NormalizationMode.Bidirectional;

    /// <summary>
    /// Strategy for handling critical playback errors.
    /// </summary>
    /// <remarks>
    /// <para>Determines whether manual user action is required or if automatic recovery is allowed.</para>
    /// <para>If automatic mode is selected, the specific queue action is specified by the <see cref="PlaybackFailureBehavior"/> property.</para>
    /// <para>Does NOT apply to:</para>
    /// <list type="bullet">
    ///   <item>BotDetectionException — always displays a dialog with a timer</item>
    ///   <item>LoginRequiredException — always displays a dialog/notification requiring user attention</item>
    /// </list>
    /// </remarks>
    public PlaybackErrorBehavior CriticalErrorBehavior { get; set; } = PlaybackErrorBehavior.ToastAndSkip;

    /// <summary>
    /// Notification display mode during complex n-token deciphering.
    /// </summary>
    public NTokenNotificationMode NTokenNotificationMode { get; set; } = NTokenNotificationMode.PanelOnly;

    /// <summary>
    /// Automatic recovery action after a critical playback error.
    /// </summary>
    /// <remarks>
    /// Applies only if <see cref="CriticalErrorBehavior"/> allows automatic recovery.
    /// </remarks>
    public PlaybackFailureBehavior PlaybackFailureBehavior { get; set; } = PlaybackFailureBehavior.SkipAndPause;

    /// <summary>
    /// Play a sound on playback error.
    /// </summary>
    public bool PlayErrorSound { get; set; } = true;

    /// <summary>
    /// Skip tracks that require complex n-token deciphering.
    /// </summary>
    public bool SkipNTokenTracks { get; set; } = false;

    /// <summary>
    /// Gets or sets whether the built-in parametric/graphic equalizer is enabled.
    /// </summary>
    public bool EqualizerEnabled { get; set; } = false;

    /// <summary>
    /// Force apply the global equalizer profile to all tracks,
    /// ignoring individual <see cref="TrackInfo.EqualizerPresetId"/> profiles.
    /// </summary>
    public bool ForceGlobalEqualizer { get; set; } = false;

    /// <summary>
    /// Manual equalizer preamp gain in dB (-12..+12).
    /// </summary>
    public float EqualizerPreampDb { get; set; } = 0f;

    /// <summary>
    /// Use simplified 5-band UI configuration mode instead of 10-band.
    /// </summary>
    public bool UseSimple5BandMode { get; set; } = true;

    /// <summary>
    /// Identifier of the active global equalizer preset.
    /// </summary>
    public string GlobalEqualizerPresetId { get; set; } = Audio.Helpers.EqualizerPresets.FlatId;

    /// <summary>
    /// Values of the 10 ISO bands for <c>custom</c> user mode (in dB).
    /// </summary>
    public float[] CustomEqualizerBands { get; set; } = new float[Audio.Helpers.EqualizerProcessor.BandCount];

    /// <summary>
    /// List of user-saved custom equalizer presets.
    /// </summary>
    public List<LMP.Core.Audio.Helpers.EqualizerPreset> UserEqualizerPresets { get; set; } = [];
}

/// <summary>
/// Notification center settings: behavior, layout, and auto-cleanup.
/// <para>
/// A single point of configuration for everything related to notifications.
/// <see cref="NotificationService"/> reads these values directly,
/// eliminating hardcoded constants in the service code.
/// </para>
/// </summary>
public sealed class NotificationSettings
{
    /// <summary>
    /// Notification panel width in pixels.
    /// </summary>
    public int PanelWidth { get; set; } = 360;

    /// <summary>
    /// Maximum number of notifications kept in memory and in the panel.
    /// Oldest notifications are evicted when exceeded.
    /// </summary>
    public int MaxInPanelCount { get; set; } = 50;

    /// <summary>
    /// Enable auto-cleanup of notifications by age.
    /// </summary>
    public bool AutoCleanupEnabled { get; set; } = true;

    /// <summary>
    /// Notification age in hours after which it is automatically deleted.
    /// Default is 48 hours (2 days).
    /// </summary>
    public int AutoCleanupAfterHours { get; set; } = 48;

    /// <summary>
    /// Background auto-cleanup check interval in minutes.
    /// </summary>
    public int CleanupCheckIntervalMinutes { get; set; } = 30;
}

/// <summary>
/// Automatic memory cleanup settings.
/// </summary>
public sealed class MemorySettings
{
    /// <summary>
    /// Enable timer-based automatic memory cleanup.
    /// </summary>
    public bool AutoCleanupEnabled { get; set; } = true;

    /// <summary>
    /// Auto-cleanup interval in minutes.
    /// </summary>
    public int AutoCleanupIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Memory threshold (MB) exceeding which triggers aggressive cleanup.
    /// 0 = disabled.
    /// </summary>
    public int PressureThresholdMb { get; set; } = 400;
}

/// <summary>
/// Automatic application update settings.
/// </summary>
public sealed class UpdateSettings
{
    /// <summary>Enable automatic update checks.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Update check interval in hours.</summary>
    public int UpdateCheckIntervalHours { get; set; } = 2;

    /// <summary>Timestamp of the last update check in UTC.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }
}

/// <summary>
/// Detailed configuration for Discord Rich Presence integration.
/// </summary>
public sealed class DiscordRpcSettings
{
    /// <summary>
    /// Optional custom Discord Application ID (17–22 digit snowflake).
    /// When empty, <see cref="Services.DiscordRpcService.DefaultClientId"/> is used.
    /// </summary>
    public string CustomClientId { get; set; } = string.Empty;

    /// <summary>
    /// Activity verb displayed in Discord (Listening / Playing / Watching).
    /// </summary>
    public DiscordRpcActivityType ActivityType { get; set; } = DiscordRpcActivityType.Listening;

    /// <summary>
    /// Field highlighted in the Discord member list status text (<c>status_display_type</c>).
    /// </summary>
    public DiscordRpcStatusDisplay StatusDisplay { get; set; } = DiscordRpcStatusDisplay.TrackTitle;

    /// <summary>
    /// Progress timestamp display mode (ProgressBar, ElapsedOnly, or None).
    /// </summary>
    public DiscordRpcTimestampMode TimestampMode { get; set; } = DiscordRpcTimestampMode.ProgressBar;

    /// <summary>
    /// Formatting mode for the primary <c>details</c> line.
    /// </summary>
    public DiscordRpcDetailsFormat DetailsFormat { get; set; } = DiscordRpcDetailsFormat.TrackTitle;

    /// <summary>
    /// Behavior when playback is paused.
    /// </summary>
    public DiscordRpcPausedBehavior PausedBehavior { get; set; } = DiscordRpcPausedBehavior.ShowPaused;

    /// <summary>
    /// Display the track's HTTPS cover art URL as the large presence image.
    /// </summary>
    public bool ShowCoverArt { get; set; } = true;

    /// <summary>
    /// Display a clickable "Listen on YouTube" button for YouTube tracks.
    /// </summary>
    public bool ShowTrackButton { get; set; } = true;

    /// <summary>
    /// Display a clickable button linking to the LMP GitHub repository.
    /// </summary>
    public bool ShowGitHubButton { get; set; } = false;

    /// <summary>
    /// Include the active playlist name when playing from a playlist.
    /// </summary>
    public bool ShowPlaylistName { get; set; } = true;

    /// <summary>
    /// Share a clickable YouTube link (button and badge link) when playing a strictly public playlist.
    /// Private, unlisted, and local playlists are never exposed.
    /// </summary>
    public bool ShowPublicPlaylistLink { get; set; } = true;

    /// <summary>
    /// Display a compact monochrome heart indicator (♥ when liked, ♡ when not liked).
    /// </summary>
    public bool ShowLikeStatus { get; set; } = true;

    /// <summary>
    /// Display the active repeat mode indicator (↻1 / ↻).
    /// </summary>
    public bool ShowRepeatMode { get; set; } = true;

    /// <summary>
    /// Display the shuffle mode indicator (⇄) when shuffle is enabled.
    /// </summary>
    public bool ShowShuffleMode { get; set; } = true;

    /// <summary>
    /// Display current queue position (e.g. 1/31) when the queue has multiple tracks.
    /// </summary>
    public bool ShowQueuePosition { get; set; } = false;

    /// <summary>
    /// Display a clickable link button to the listener's own YouTube channel (@handle).
    /// Disabled by default for privacy.
    /// </summary>
    public bool ShowListenerChannel { get; set; } = false;

    /// <summary>
    /// Strip a redundant leading "Artist - " prefix from YouTube track titles when it matches the track author.
    /// </summary>
    public bool StripAuthorFromTitle { get; set; } = true;

    /// <summary>
    /// Style of the small corner badge rendered over the cover art in Discord.
    /// </summary>
    public DiscordRpcBadgeStyle BadgeStyle { get; set; } = DiscordRpcBadgeStyle.PublicPlaylistCover;

    /// <summary>
    /// Custom HTTPS URL (PNG/WebP/GIF/AVIF) used when <see cref="BadgeStyle"/> is <see cref="DiscordRpcBadgeStyle.Custom"/>.
    /// </summary>
    public string CustomBadgeUrl { get; set; } = string.Empty;

    /// <summary>
    /// Show an idle status when no track is loaded instead of clearing presence.
    /// </summary>
    public bool ShowIdleStatus { get; set; } = false;
}

/// <summary>
/// Application settings. Stored as JSON in Settings table.
/// </summary>
public sealed class AppSettings
{
    // === Audio (1-7) ===

    /// <summary>
    /// Current player volume level in percent (0–100).
    /// </summary>
    public int Volume { get; set; } = 50;

    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; } = RepeatMode.None;
    public int MaxVolumeLimit { get; set; } = 100;
    public AudioQualityPreference QualityPreference { get; set; } = AudioQualityPreference.BestAvailable;
    public bool RememberTrackFormat { get; set; } = true;

    /// <summary>
    /// Advanced audio settings (8).
    /// </summary>
    public AudioSettings Audio { get; set; } = new();

    // === Network (9-11) ===
    public InternetProfile InternetProfile { get; set; } = InternetProfile.Medium;
    public CloudSyncBatchInterval CloudBatchInterval { get; set; } = CloudSyncBatchInterval.Min1;
    public ProxySettings Proxy { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();

    // === UI (12-24) ===
    public double PlaylistHeaderHeight { get; set; } = 300;
    public string LanguageCode { get; set; } = "en";
    public string DownloadPath { get; set; } = string.Empty;
    public bool DiscordRpcEnabled { get; set; } = true;
    public DiscordRpcSettings DiscordRpc { get; set; } = new();
    public bool SystemMediaTransportEnabled { get; set; } = true;
    public bool AutoPlayOnUrlPaste { get; set; } = true;
    public int LoadBatchSize { get; set; } = 20;
    public int SearchBatchSize { get; set; } = 30;
    public bool EnableSearchCache { get; set; } = true;
    public int SearchCacheTtlMinutes { get; set; } = 120;

    /// <summary>
    /// Maximum number of displayed suggestions in the search bar and chip bar (3–15).
    /// Default: 8.
    /// </summary>
    public int MaxSuggestionsCount { get; set; } = 8;

    /// <summary>
    /// Optimize UI when the window loses focus.
    ///
    /// <para><b>true:</b> Pauses heavy UI updates (position, buffer, animations)
    /// on minimize or focus loss. Saves CPU.</para>
    ///
    /// <para><b>false:</b> UI operates normally even without focus.
    /// Useful for dual-monitor setups.</para>
    ///
    /// <para>This setting does NOT affect tray minimization — optimization is always active there.</para>
    /// </summary>
    public bool OptimizeWhenInactive { get; set; } = true;

    /// <summary>
    /// Action when clicking the window close button.
    /// Default is to ask.
    /// </summary>
    public CloseAction CloseAction { get; set; } = CloseAction.Ask;

    /// <summary>
    /// Minimize to system tray on minimize button click.
    /// Default is false — standard taskbar minimization.
    /// </summary>
    public bool MinimizeToTray { get; set; } = false;

    /// <summary>
    /// Notification panel and auto-cleanup settings (25).
    /// </summary>
    public NotificationSettings Notifications { get; set; } = new();

    /// <summary>
    /// YouTube likes synchronization mode (26).
    /// </summary>
    public LikeSyncMode LikeSyncMode { get; set; } = LikeSyncMode.MusicOnly;

    /// <summary>
    /// Memory management settings (27).
    /// </summary>
    public MemorySettings Memory { get; set; } = new();

    /// <summary>
    /// Application update settings (28).
    /// </summary>
    public UpdateSettings Updates { get; set; } = new();

    /// <summary>
    /// Use live waveform animation for the currently playing track.
    /// false = classic pulsing highlight.
    /// </summary>
    public bool UseWaveAnimation { get; set; } = true;

    /// <summary>
    /// Enables dynamic brightness lift, spring stroke thickness, and soft 2-pass bloom on the active track border.
    /// </summary>
    public bool WaveBorderReactiveGlow { get; set; } = true;

    /// <summary>
    /// Animation speed of the playing track in the list.
    /// </summary>
    public TrackAnimationSpeed TrackAnimationSpeed { get; set; } = TrackAnimationSpeed.Medium;

    /// <summary>
    /// Visual style of the ambient playlist/queue backdrop.
    /// </summary>
    public AmbientBackdropMode AmbientBackdropMode { get; set; } = AmbientBackdropMode.FluidWave;

    /// <summary>
    /// Intensity multiplier for the ambient playlist/queue backdrop in percent (20–150, default 100).
    /// </summary>
    public int AmbientBackdropIntensity { get; set; } = 100;

    /// <summary>
    /// Dynamic brightness reactivity of the ambient backdrop to track loudness and beats in percent (0–100, default 60).
    /// 0 = steady calm brightness without flashing; 100 = full verse-to-drop dynamic range.
    /// </summary>
    public int AmbientBackdropReactivity { get; set; } = 60;

    /// <summary>
    /// List of user-saved custom color themes.
    /// </summary>
    public List<ThemeSettings> UserThemes { get; set; } = [];
}