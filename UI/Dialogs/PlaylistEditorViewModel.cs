using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Dialogs;

/// <summary>
/// Playlist artwork selection mode.
/// </summary>
public enum CoverMode
{
    /// <summary>Direct HTTP/HTTPS URL input.</summary>
    Url,

    /// <summary>Generate a mosaic from the track artwork of the current playlist.</summary>
    FromTracks,

    /// <summary>Select a local image file from the file system.</summary>
    File
}

/// <summary>
/// ViewModel for the playlist metadata editor.
/// Supports a two-column layout, reactive preview, and automatic palette synchronization.
/// </summary>
public sealed partial class PlaylistEditorViewModel : ViewModelBase
{
    private readonly NetworkManager? _networkManager;
    private readonly DominantColorService? _dominantColorService;
    private readonly string? _originalDescription;

    /// <summary>
    /// Playlist title.
    /// </summary>
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    /// <summary>
    /// URL or local path to the playlist artwork.
    /// </summary>
    [ObservableProperty]
    public partial string? ThumbnailUrl { get; set; }

    /// <summary>
    /// Custom accent color in HEX format (e.g., #FF5500).
    /// </summary>
    [ObservableProperty]
    public partial string? CustomColor { get; set; }

    /// <summary>
    /// Custom text description of the playlist.
    /// </summary>
    [ObservableProperty]
    public partial string? Description { get; set; }

    /// <summary>
    /// Automatically calculated dominant artwork color in HEX format.
    /// </summary>
    [ObservableProperty]
    public partial string? ComputedColor { get; set; }

    /// <summary>
    /// Brush for rendering the active color preview.
    /// </summary>
    [ObservableProperty]
    public partial IBrush ComputedColorPreviewBrush { get; set; } = Brushes.Transparent;

    /// <summary>
    /// String representation of the active color for display in the compact palette card.
    /// </summary>
    [ObservableProperty]
    public partial string EffectiveColorText { get; set; } = "Auto";

    /// <summary>
    /// Flag indicating an ongoing asynchronous recalculation of the dominant color from the image.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecalculateColorCommand))]
    public partial bool IsRecalculatingColor { get; set; }

    /// <summary>
    /// Command to force recalculation of the dominant color from the active artwork.
    /// </summary>
    public IAsyncRelayCommand RecalculateColorCommand { get; }

    /// <summary>
    /// Flag indicating a system immutable playlist (e.g., "Favorite Tracks").
    /// </summary>
    public bool IsSystemPlaylist { get; }

    /// <summary>
    /// Indicates whether the title is available for manual editing.
    /// </summary>
    public bool IsNameEditable { get; }

    /// <summary>
    /// Flag indicating edit mode for an existing playlist.
    /// </summary>
    public bool IsForEdit { get; }

    /// <summary>
    /// Callback delegate to trigger the creation of a local playlist copy.
    /// </summary>
    public Action? OnCreateCopy { get; set; }

    /// <summary>
    /// Command to create a local copy of the playlist.
    /// </summary>
    public IRelayCommand CreateCopyCommand { get; }

    /// <summary>
    /// Active artwork source selection mode.
    /// </summary>
    [ObservableProperty]
    public partial CoverMode SelectedCoverMode { get; set; } = CoverMode.Url;

    /// <summary>
    /// Flag indicating whether the direct URL tab is active.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCoverModeUrl { get; set; } = true;

    /// <summary>
    /// Flag indicating whether the track mosaic tab is active.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCoverModeFromTracks { get; set; }

    /// <summary>
    /// Flag indicating whether the local file tab is active.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCoverModeFile { get; set; }

    /// <summary>
    /// ViewModel for selecting artwork to build a mosaic.
    /// </summary>
    [ObservableProperty]
    public partial PlaylistCoverPickerViewModel? CoverPicker { get; set; }

    /// <summary>
    /// Flag indicating whether the artwork mode selector is visible.
    /// </summary>
    public bool ShowCoverModeSwitch { get; } = true;

    /// <summary>
    /// Indicates whether track mosaic mode is available (requires tracks with artwork).
    /// </summary>
    public bool HasTracksCoverOption { get; }

    /// <summary>
    /// Path to the file selected by the user via the system dialog.
    /// </summary>
    [ObservableProperty]
    public partial string? SelectedFilePath { get; set; }

    /// <summary>
    /// Command to open the system open file dialog for images.
    /// </summary>
    public IAsyncRelayCommand SelectFileCommand { get; }

    /// <summary>
    /// Flag indicating visibility of the YouTube Music cloud sync section.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowSyncSection { get; set; }

    /// <summary>
    /// Flag enabling two-way sync with YouTube Music.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSyncedToCloud { get; set; }

    /// <summary>
    /// Flag indicating authorization of the current user session.
    /// </summary>
    public bool IsAuthenticated { get; }

    /// <summary>
    /// Flag indicating the presence of a linked remote YouTube ID.
    /// </summary>
    public bool HasYoutubeBinding { get; }

    /// <summary>
    /// Initial state of the sync flag when the dialog was opened.
    /// </summary>
    public bool OriginalSyncState { get; }

    /// <summary>
    /// Text of the current form data validation error.
    /// </summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>
    /// Flag indicating the presence of validation errors.
    /// </summary>
    [ObservableProperty]
    public partial bool HasErrors { get; set; }

    /// <summary>
    /// Indicates whether the form can be saved in its current state.
    /// </summary>
    public bool CanSave => !HasErrors;

    /// <summary>
    /// Preview URL for loading over the network or from application resources.
    /// </summary>
    [ObservableProperty]
    public partial string? ThumbnailPreviewUrl { get; set; }

    /// <summary>
    /// Flag indicating whether a cover preview of any type is available.
    /// </summary>
    [ObservableProperty]
    public partial bool HasThumbnailPreview { get; set; }

    /// <summary>
    /// Flag indicating a network source for the preview (HTTP/HTTPS/avares).
    /// </summary>
    [ObservableProperty]
    public partial bool IsPreviewHttp { get; set; }

    /// <summary>
    /// Flag indicating a local source for the preview (disk file or in-memory bitmap).
    /// </summary>
    [ObservableProperty]
    public partial bool IsPreviewLocal { get; set; }

    /// <summary>
    /// Local bitmap snapshot for instant display of a mosaic or local file.
    /// </summary>
    [ObservableProperty]
    public partial Bitmap? LocalPreviewBitmap { get; set; }

    private readonly DispatcherTimer _thumbnailDebounceTimer;

    /// <summary>
    /// Initializes a new instance of <see cref="PlaylistEditorViewModel"/>.
    /// </summary>
    /// <param name="name">Playlist title.</param>
    /// <param name="thumbnailUrl">Cover URL or path.</param>
    /// <param name="customColor">Custom color in HEX format.</param>
    /// <param name="description">Playlist description.</param>
    /// <param name="computedColor">Previously calculated dominant color.</param>
    /// <param name="showSync">Flag indicating whether sync settings are available.</param>
    /// <param name="isSynced">Initial sync state.</param>
    /// <param name="isAuthenticated">Indicates whether the user is authenticated.</param>
    /// <param name="hasYoutubeBinding">Indicates whether the playlist is bound to a YouTube ID.</param>
    /// <param name="playlistTracks">Collection of playlist tracks for mosaic generation.</param>
    /// <param name="isForEdit">Indicates whether the editor is created for an existing playlist.</param>
    /// <param name="isSystemPlaylist">Indicates whether the playlist is a system playlist.</param>
    /// <param name="networkManager">Network request manager.</param>
    /// <param name="dominantColorService">Color palette analysis service.</param>
    public PlaylistEditorViewModel(
        string name = "",
        string? thumbnailUrl = null,
        string? customColor = null,
        string? description = null,
        string? computedColor = null,
        bool showSync = false,
        bool isSynced = false,
        bool isAuthenticated = false,
        bool hasYoutubeBinding = false,
        IReadOnlyList<TrackInfo>? playlistTracks = null,
        bool isForEdit = false,
        bool isSystemPlaylist = false,
        NetworkManager? networkManager = null,
        DominantColorService? dominantColorService = null)
    {
        _networkManager = networkManager ?? AppEntry.Services.GetService<NetworkManager>();
        _dominantColorService = dominantColorService ?? AppEntry.Services.GetService<DominantColorService>();
        _originalDescription = description;
        IsForEdit = isForEdit;
        IsSystemPlaylist = isSystemPlaylist;
        IsNameEditable = !isSystemPlaylist;
        ComputedColor = computedColor;
        ShowSyncSection = showSync;
        IsSyncedToCloud = isSynced;
        OriginalSyncState = isSynced;
        IsAuthenticated = isAuthenticated;
        HasYoutubeBinding = hasYoutubeBinding;

        UpdateColorVisuals(computedColor, customColor);

        _thumbnailDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _thumbnailDebounceTimer.Tick += (s, e) =>
        {
            _thumbnailDebounceTimer.Stop();
            UpdateThumbnailPreview(ThumbnailUrl);
            _ = AutoRecalculateColorAsync();
        };

        HasTracksCoverOption = playlistTracks != null && playlistTracks.Any(t => t.HasThumbnail) && _networkManager != null;
        if (HasTracksCoverOption && _networkManager != null)
        {
            CoverPicker = new PlaylistCoverPickerViewModel(playlistTracks!, _networkManager);
            CoverPicker.OnPreviewUpdated += (bitmap) =>
            {
                if (SelectedCoverMode == CoverMode.FromTracks)
                {
                    LocalPreviewBitmap = bitmap;
                    HasThumbnailPreview = bitmap != null;
                    IsPreviewLocal = bitmap != null;
                    IsPreviewHttp = false;
                }
            };
        }

        SelectFileCommand = new AsyncRelayCommand(SelectFileAsync);
        RecalculateColorCommand = new AsyncRelayCommand(
            RecalculateColorFromCoverAsync,
            () => (!string.IsNullOrWhiteSpace(ThumbnailUrl) || LocalPreviewBitmap != null) && !IsRecalculatingColor);

        CreateCopyCommand = new RelayCommand(() => OnCreateCopy?.Invoke());

        Name = name;
        ThumbnailUrl = thumbnailUrl;
        CustomColor = customColor;
        Description = description;

        if (string.IsNullOrWhiteSpace(thumbnailUrl) && HasTracksCoverOption)
            SelectedCoverMode = CoverMode.FromTracks;
        else
            SelectedCoverMode = CoverMode.Url;

        SyncCoverModes(SelectedCoverMode);
        UpdateValidation();
        UpdateThumbnailPreview(ThumbnailUrl);
    }

    partial void OnSelectedCoverModeChanged(CoverMode value) => SyncCoverModes(value);

    private void SyncCoverModes(CoverMode mode)
    {
        IsCoverModeUrl = mode == CoverMode.Url;
        IsCoverModeFromTracks = mode == CoverMode.FromTracks;
        IsCoverModeFile = mode == CoverMode.File;

        if (mode == CoverMode.FromTracks && CoverPicker?.MosaicPreview != null)
        {
            LocalPreviewBitmap = CoverPicker.MosaicPreview;
            HasThumbnailPreview = true;
            IsPreviewLocal = true;
            IsPreviewHttp = false;
        }
        else if (mode == CoverMode.Url)
        {
            UpdateThumbnailPreview(ThumbnailUrl);
        }
        else if (mode == CoverMode.File)
        {
            UpdateThumbnailPreview(SelectedFilePath);
        }
    }

    partial void OnNameChanged(string value) => UpdateValidation();

    partial void OnThumbnailUrlChanged(string? value)
    {
        UpdateValidation();
        RecalculateColorCommand?.NotifyCanExecuteChanged();
        if (_thumbnailDebounceTimer is not null)
        {
            _thumbnailDebounceTimer.Stop();
            _thumbnailDebounceTimer.Start();
        }
    }

    /// <summary>Activates the URL cover input mode.</summary>
    public void SetCoverModeUrl() => SelectedCoverMode = CoverMode.Url;

    /// <summary>Activates the track mosaic generation mode.</summary>
    public void SetCoverModeFromTracks() => SelectedCoverMode = CoverMode.FromTracks;

    /// <summary>Activates the local file selection mode.</summary>
    public void SetCoverModeFile() => SelectedCoverMode = CoverMode.File;

    private async Task SelectFileAsync(CancellationToken ct)
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = SL["CoverPicker_SelectFile"],
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(SL["CoverPicker_ImageFiles"])
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"],
                    MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/bmp"]
                }
            ]
        });

        if (files.Count == 0) return;

        var filePath = files[0].Path.LocalPath;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        SelectedFilePath = filePath;
        ThumbnailUrl = filePath;
        UpdateThumbnailPreview(filePath);
        _ = AutoRecalculateColorAsync();
    }

    private static Window? GetTopLevel()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            for (int i = 0; i < desktop.Windows.Count; i++)
            {
                if (desktop.Windows[i].IsActive) return desktop.Windows[i];
            }
            return desktop.MainWindow;
        }
        return null;
    }

    private void UpdateColorVisuals(string? comp, string? cust)
    {
        var active = !string.IsNullOrWhiteSpace(cust) ? cust : comp;
        if (TryParseColor(active, out var brush, out var hex))
        {
            ComputedColorPreviewBrush = brush;
            EffectiveColorText = hex;
        }
        else
        {
            ComputedColorPreviewBrush = Brushes.Transparent;
            EffectiveColorText = "Auto";
        }
    }

    private async Task AutoRecalculateColorAsync()
    {
        if (string.IsNullOrWhiteSpace(ThumbnailUrl) || !IsValidUri(ThumbnailUrl))
            return;

        await RecalculateColorFromCoverAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task RecalculateColorFromCoverAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ThumbnailUrl) && LocalPreviewBitmap == null) return;

        IsRecalculatingColor = true;
        try
        {
            if (_dominantColorService == null) return;

            var targetSource = ThumbnailUrl;
            if (string.IsNullOrEmpty(targetSource) && SelectedCoverMode == CoverMode.FromTracks && CoverPicker != null)
                targetSource = await CoverPicker.PersistMosaicAsync(ct).ConfigureAwait(true);

            if (string.IsNullOrEmpty(targetSource)) return;

            var color = await _dominantColorService.GetDominantColorAsync(targetSource, ct);
            if (color.HasValue)
            {
                var hex = $"#{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}";
                ComputedColor = hex;
                CustomColor = null;
                UpdateColorVisuals(hex, null);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistEditor] Color recalculation failed: {ex.Message}");
        }
        finally
        {
            IsRecalculatingColor = false;
        }
    }

    private void UpdateThumbnailPreview(string? url)
    {
        var oldBitmap = LocalPreviewBitmap;

        if (string.IsNullOrWhiteSpace(url))
        {
            HasThumbnailPreview = false;
            IsPreviewHttp = false;
            IsPreviewLocal = false;
            ThumbnailPreviewUrl = null;
            LocalPreviewBitmap = null;
            oldBitmap?.Dispose();
            return;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
        {
            HasThumbnailPreview = true;
            IsPreviewHttp = true;
            IsPreviewLocal = false;
            ThumbnailPreviewUrl = url;
            LocalPreviewBitmap = null;
            oldBitmap?.Dispose();
            return;
        }

        var localPath = ResolveLocalPath(url);
        if (localPath != null && File.Exists(localPath))
        {
            try
            {
                using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bitmap = new Bitmap(stream);
                HasThumbnailPreview = true;
                IsPreviewHttp = false;
                IsPreviewLocal = true;
                ThumbnailPreviewUrl = null;
                LocalPreviewBitmap = bitmap;
                oldBitmap?.Dispose();
            }
            catch
            {
                HasThumbnailPreview = false;
                IsPreviewHttp = false;
                IsPreviewLocal = false;
                ThumbnailPreviewUrl = null;
                LocalPreviewBitmap = null;
                oldBitmap?.Dispose();
            }
            return;
        }

        HasThumbnailPreview = false;
        IsPreviewHttp = false;
        IsPreviewLocal = false;
        ThumbnailPreviewUrl = null;
        LocalPreviewBitmap = null;
        oldBitmap?.Dispose();
    }

    private static string? ResolveLocalPath(string url)
    {
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var fileUri))
                return fileUri.LocalPath;
            return null;
        }
        return Path.IsPathRooted(url) ? url : null;
    }

    private void UpdateValidation()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            SetError(SL["Error_EmptyName"]);
            return;
        }

        if (!string.IsNullOrWhiteSpace(ThumbnailUrl) && !IsValidUri(ThumbnailUrl))
        {
            SetError(SL["Error_InvalidUrl"]);
            return;
        }

        ClearError();
    }

    private void SetError(string msg)
    {
        ErrorMessage = msg;
        HasErrors = true;
        OnPropertyChanged(nameof(CanSave));
    }

    private void ClearError()
    {
        ErrorMessage = null;
        HasErrors = false;
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>
    /// Checks whether the provided string represents a valid local path or a supported URI scheme.
    /// </summary>
    public static bool IsValidUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (Path.IsPathRooted(url)) return true;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Scheme is "http" or "https" or "avares" or "file";
        }
        return false;
    }

    private static bool TryParseColor(string? colorStr, out IBrush brush, out string hex)
    {
        brush = Brushes.Transparent;
        hex = "Auto";
        if (string.IsNullOrWhiteSpace(colorStr)) return false;
        try
        {
            var parsed = Color.Parse(colorStr);
            brush = new SolidColorBrush(parsed);
            hex = $"#{parsed.R:X2}{parsed.G:X2}{parsed.B:X2}";
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Creates an editor model for the new playlist creation scenario.
    /// </summary>
    public static PlaylistEditorViewModel ForCreate(
        bool isAuthenticated = false,
        NetworkManager? networkManager = null,
        DominantColorService? dominantColorService = null) =>
        new(name: "", thumbnailUrl: null, customColor: null, description: null,
            computedColor: null,
            showSync: isAuthenticated,
            isSynced: isAuthenticated,
            isAuthenticated: isAuthenticated,
            hasYoutubeBinding: false,
            playlistTracks: null,
            isForEdit: false,
            isSystemPlaylist: false,
            networkManager: networkManager,
            dominantColorService: dominantColorService);

    /// <summary>
    /// Creates an editor model for the existing playlist editing scenario.
    /// </summary>
    public static PlaylistEditorViewModel ForEdit(
        Playlist playlist,
        bool isAuthenticated,
        IReadOnlyList<TrackInfo>? playlistTracks = null,
        NetworkManager? networkManager = null,
        DominantColorService? dominantColorService = null)
    {
        var isSystem = LibraryService.IsSystemPlaylist(playlist.Id);
        return new(
            name: playlist.Name,
            thumbnailUrl: playlist.ThumbnailUrl,
            customColor: playlist.CustomColor,
            description: playlist.Description,
            computedColor: playlist.ComputedColor,
            showSync: !isSystem && (isAuthenticated || playlist.IsFromAccount),
            isSynced: playlist.IsFromAccount,
            isAuthenticated: isAuthenticated,
            hasYoutubeBinding: !string.IsNullOrEmpty(playlist.YoutubeId),
            playlistTracks: playlistTracks,
            isForEdit: true,
            isSystemPlaylist: isSystem,
            networkManager: networkManager,
            dominantColorService: dominantColorService);
    }

    /// <summary>
    /// Exports the state of form fields into an immutable <see cref="EditPlaylistResult"/>.
    /// </summary>
    public EditPlaylistResult ToResult() => new()
    {
        Name = Name.Trim(),
        ThumbnailUrl = string.IsNullOrWhiteSpace(ThumbnailUrl) ? null : ThumbnailUrl.Trim(),
        CustomColor = string.IsNullOrWhiteSpace(CustomColor) ? null : CustomColor.Trim(),
        Description = Description?.Trim(),
        ComputedColor = ComputedColor
    };

    /// <summary>
    /// Flag indicating whether the cloud sync state has changed relative to the initial state.
    /// </summary>
    public bool SyncStateChanged => IsSyncedToCloud != OriginalSyncState;

    /// <summary>
    /// Flag indicating whether the text description has changed relative to the initial value.
    /// </summary>
    public bool DescriptionChanged => !string.Equals(Description?.Trim(), _originalDescription?.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Releases resources used by the timer, bitmap previews, and child mosaic model.
    /// </summary>
    /// <param name="disposing">Flag indicating whether invocation comes from Dispose.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnailDebounceTimer.Stop();
            LocalPreviewBitmap?.Dispose();
            CoverPicker?.Dispose();
        }
        base.Dispose(disposing);
    }
}