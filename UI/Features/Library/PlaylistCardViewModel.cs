using Avalonia.Media;

namespace LMP.UI.Features.Library;

/// <summary>
/// ViewModel for a playlist card in the library.
/// </summary>
public sealed partial class PlaylistCardViewModel : ViewModelBase
{
    #region Fields

    private readonly CookieAuthService _auth;
    private readonly PlayerControlService _playerControl;

    private readonly Func<string, Task>? _onDelete;
    private readonly Func<Core.Models.Playlist, Task>? _onEdit;
    private readonly Func<Core.Models.Playlist, Task> _addToQueueAction;
    private readonly Func<Core.Models.Playlist, Task> _playAction;
    private readonly Action<string> _onOpen;

    private readonly EventHandler<string> _languageChangedHandler;
    private bool _isDisposed;

    #endregion

    #region Properties — Identity

    /// <summary>
    /// The underlying playlist entity.
    /// Mutable — updated via <see cref="UpdateFrom"/>.
    /// </summary>
    public Core.Models.Playlist Playlist { get; }

    /// <summary>
    /// Unique identifier of the playlist.
    /// </summary>
    public string Id => Playlist.Id;

    /// <summary>
    /// Indicates whether this is the system "Liked Songs" playlist (special UI: heart cover, cannot be deleted).
    /// </summary>
    public bool IsLikedPlaylist => Playlist.Id == LibraryService.LikedPlaylistId;

    #endregion

    #region Properties — Display

    /// <summary>
    /// Playlist title. Updated on rename and language changes.
    /// </summary>
    [ObservableProperty] public partial string Name { get; private set; }

    /// <summary>
    /// Cover art URL upscaled for YouTube thumbnails. Updated when the cover changes.
    /// </summary>
    [ObservableProperty] public partial string? ThumbnailUrl { get; private set; }

    /// <summary>
    /// Track count in the playlist. Updated when tracks are added/removed.
    /// </summary>
    [ObservableProperty] public partial int TrackCount { get; set; }

    /// <summary>
    /// Card visibility state. Managed by stagger animation in <see cref="LibraryViewModel"/>.
    /// </summary>
    [ObservableProperty] public partial bool IsVisible { get; set; }

    partial void OnTrackCountChanged(int value)
    {
        OnPropertyChanged(nameof(FormattedTrackCount));
    }

    partial void OnThumbnailUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    /// <summary>
    /// Formatted track count with localized pluralization.
    /// </summary>
    public string FormattedTrackCount => FormatTrackCount(TrackCount);

    /// <summary>
    /// Gets a value indicating whether a valid cover art image exists (non-empty and not Liked).
    /// </summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl) && !IsLikedPlaylist;

    /// <summary>
    /// Gets a value indicating whether to display a placeholder instead of cover art.
    /// </summary>
    public bool ShowPlaceholder => string.IsNullOrEmpty(ThumbnailUrl) && !IsLikedPlaylist;

    /// <summary>
    /// Flag indicating whether the card's context menu is open (controls the menu-open pseudoclass).
    /// </summary>
    [ObservableProperty] public partial bool IsMenuOpen { get; set; }

    /// <summary>
    /// YouTube playlist URL for CopyLinkButton.
    /// Null if the playlist is not bound to YouTube.
    /// </summary>
    public string? YoutubeUrl
    {
        get
        {
            if (IsLikedPlaylist && _auth.IsAuthenticated)
                return "https://www.youtube.com/playlist?list=LM";
            return string.IsNullOrEmpty(Playlist.YoutubeId)
                ? null
                : $"https://www.youtube.com/playlist?list={Playlist.YoutubeId}";
        }
    }

    /// <summary>
    /// Gets a value indicating whether the YouTube playlist link can be copied.
    /// </summary>
    public bool CanCopyLink => !string.IsNullOrEmpty(YoutubeUrl);

    #endregion

    #region Properties — Author & Ownership

    /// <summary>
    /// Name of the playlist author/owner.
    /// </summary>
    [ObservableProperty] public partial string? Author { get; private set; }

    /// <summary>
    /// Gets a value indicating whether to display the author line on the card.
    /// True only for third-party playlists with a known author.
    /// </summary>
    [ObservableProperty] public partial bool ShowAuthor { get; private set; }

    /// <summary>
    /// Display string for the author with "by X" prefix.
    /// </summary>
    [ObservableProperty] public partial string? AuthorDisplayText { get; private set; }

    /// <summary>
    /// Tooltip for the author line: "Playlist by X".
    /// </summary>
    [ObservableProperty] public partial string? AuthorTooltip { get; private set; }

    /// <summary>
    /// Playlist is private (🔒).
    /// </summary>
    [ObservableProperty] public partial bool IsPrivate { get; private set; }

    /// <summary>
    /// Playlist is unlisted / accessible via link (🔗).
    /// </summary>
    [ObservableProperty] public partial bool IsUnlisted { get; private set; }

    /// <summary>
    /// Playlist is stored locally (LocalOnly or TwoWaySync).
    /// </summary>
    public bool IsLocal => Playlist.IsLocal;

    /// <summary>
    /// Playlist is read-only (Foreign or CloudPublic).
    /// </summary>
    public bool IsReadOnly => !Playlist.IsEditable;

    /// <summary>
    /// Gets a value indicating whether the playlist can be deleted (all except Liked).
    /// </summary>
    public bool CanDelete => !IsLikedPlaylist;

    /// <summary>
    /// Gets a value indicating whether the edit playlist dialog can be opened.
    /// </summary>
    public bool CanEdit => Playlist.IsEditable;

    #endregion

    #region Properties — Cloud & Sync

    /// <summary>
    /// Playlist is linked to YouTube: has an accessible YoutubeId,
    /// or is Liked for an authenticated user.
    /// Used to show the cloud icon (☁).
    /// </summary>
    [ObservableProperty] public partial bool HasCloudSource { get; private set; }

    /// <summary>
    /// Two-way synchronization is active (TwoWaySync or Liked + auth).
    /// </summary>
    [ObservableProperty] public partial bool IsTwoWaySynced { get; private set; }

    #endregion

    #region Properties — Playback State

    /// <summary>
    /// Indicates whether this playlist is active in the player and the queue is "pristine".
    /// Used for the card border pulsing animation.
    /// </summary>
    [ObservableProperty] public partial bool IsActive { get; private set; }

    /// <summary>
    /// Indicates whether the queue exactly matches the tracks in this playlist.
    /// </summary>
    [ObservableProperty] public partial bool IsQueuePure { get; private set; }

    /// <summary>
    /// Queue is pristine and currently actively playing.
    /// Used for the Play/Pause icon in the context menu.
    /// </summary>
    [ObservableProperty] public partial bool IsPlayingPure { get; private set; }

    partial void OnIsPlayingPureChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayMenuHeader));
        OnPropertyChanged(nameof(PlayMenuIcon));
    }

    /// <summary>
    /// Display text for the "Play / Pause" context menu item.
    /// </summary>
    public string PlayMenuHeader => IsPlayingPure
        ? LocalizationService.Instance["Player_Pause"]
        : LocalizationService.Instance["Player_Play"];

    /// <summary>
    /// Path geometry for the context menu Play/Pause icon.
    /// </summary>
    public Geometry? PlayMenuIcon
    {
        get
        {
            var key = IsPlayingPure ? "Icon.Pause" : "Icon.Play";
            if (Avalonia.Application.Current?.TryGetResource(key, null, out var res) == true
                && res is Geometry geo)
                return geo;
            return null;
        }
    }

    #endregion

    #region Commands

    /// <summary>Open playlist (navigate).</summary>
    public IRelayCommand OpenCommand { get; }

    /// <summary>Delete playlist.</summary>
    public IAsyncRelayCommand DeleteCommand { get; }

    /// <summary>Add all tracks to queue.</summary>
    public IAsyncRelayCommand AddToQueueCommand { get; }

    /// <summary>Play playlist (smart play/pause).</summary>
    public IAsyncRelayCommand PlayCommand { get; }

    /// <summary>Opens the edit dialog.</summary>
    public IAsyncRelayCommand EditCommand { get; }

    /// <summary>Copies the YouTube playlist link.</summary>
    public IAsyncRelayCommand CopyLinkCommand { get; }

    #endregion

    #region Constructor

    public PlaylistCardViewModel(
        CookieAuthService auth,
        PlayerControlService playerControl,
        Core.Models.Playlist playlist,
        int trackCount,
        Action<string> onOpen,
        Func<Core.Models.Playlist, Task> addToQueueAction,
        Func<Core.Models.Playlist, Task> playAction,
        Func<string, Task>? onDelete = null,
        Func<Core.Models.Playlist, Task>? onEdit = null)
    {
        Playlist = playlist;
        _auth = auth;
        _playerControl = playerControl;
        _onOpen = onOpen;
        _addToQueueAction = addToQueueAction;
        _playAction = playAction;
        _onDelete = onDelete;
        _onEdit = onEdit;

        // ═══ Initial state from model ═══
        Name = playlist.Name;
        TrackCount = trackCount;
        ThumbnailUrl = playlist.ThumbnailUrl;
        Author = playlist.Author;

        ApplyOwnershipState(playlist);
        ApplyCloudState(playlist);

        // ═══ Commands ═══
        OpenCommand = new RelayCommand(() =>
        {
            if (!_isDisposed) _onOpen(Playlist.Id);
        });

        DeleteCommand = new AsyncRelayCommand(
            async () =>
            {
                if (!_isDisposed && _onDelete != null)
                    await _onDelete(Playlist.Id);
            },
            () => CanDelete);

        AddToQueueCommand = new AsyncRelayCommand(async () =>
        {
            if (!_isDisposed) await _addToQueueAction(Playlist);
        });

        PlayCommand = new AsyncRelayCommand(async () =>
        {
            if (!_isDisposed) await _playAction(Playlist);
        });

        EditCommand = new AsyncRelayCommand(async () =>
        {
            if (!_isDisposed && _onEdit != null) await _onEdit(Playlist);
        });

        CopyLinkCommand = new AsyncRelayCommand(
            async () =>
            {
                if (_isDisposed || string.IsNullOrEmpty(YoutubeUrl)) return;
                await Clipboard.SetTextAsync(YoutubeUrl);
                CopyHintService.Instance.Show(
                    LocalizationService.Instance["Playlist_LinkCopied"],
                    CopyHintKind.Success);
            },
            () => !string.IsNullOrEmpty(YoutubeUrl));

        // ═══ Playback state tracking (Zero-Allocation O(1) Centralized Purity) ═══
        _playerControl.PlaybackPurityChanged += OnPlaybackPurityChanged;

        _languageChangedHandler = (_, _) =>
        {
            if (IsLikedPlaylist)
            {
                Name = Playlist.Name;
            }

            OnPropertyChanged(nameof(FormattedTrackCount));
            OnPropertyChanged(nameof(PlayMenuHeader));
            RefreshAuthorTexts();
        };
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        SyncInitialPlaybackState();
    }

    private void SyncInitialPlaybackState()
    {
        bool isThis = _playerControl.ActivePlaylistId == Id;
        IsActive = isThis && _playerControl.IsQueuePure;
        IsQueuePure = isThis && _playerControl.IsQueuePure;
        IsPlayingPure = isThis && _playerControl.IsPlayingPure;
    }

    private void OnPlaybackPurityChanged(string? activeId, bool isPure, bool isPlayingPure)
    {
        if (_isDisposed) return;

        bool isThis = string.Equals(activeId, Id, StringComparison.Ordinal);
        IsActive = isThis && isPure;
        IsQueuePure = isThis && isPure;
        IsPlayingPure = isThis && isPlayingPure;
    }

    #endregion

    #region Update

    /// <summary>
    /// Updates the card using fresh playlist data.
    /// Called during incremental library updates.
    /// </summary>
    public void UpdateFrom(Core.Models.Playlist playlist, int trackCount)
    {
        if (_isDisposed) return;

        // ═══ Display ═══
        if (Name != playlist.Name)
        {
            Playlist.StoredName = playlist.StoredName;
            Name = playlist.Name;
        }

        if (TrackCount != trackCount)
            TrackCount = trackCount;

        var newUrl = playlist.ThumbnailUrl;
        if (ThumbnailUrl != newUrl)
        {
            Playlist.ThumbnailUrl = playlist.ThumbnailUrl;
            ThumbnailUrl = newUrl;
        }

        // ═══ Author ═══
        if (!string.Equals(Author, playlist.Author, StringComparison.Ordinal))
        {
            Playlist.Author = playlist.Author;
            Author = playlist.Author;
        }

        // ═══ Ownership & Visibility ═══
        bool ownershipChanged = Playlist.Ownership != playlist.Ownership
                                || Playlist.Visibility != playlist.Visibility;
        if (ownershipChanged)
        {
            Playlist.Ownership = playlist.Ownership;
            Playlist.Visibility = playlist.Visibility;
            ApplyOwnershipState(playlist);
        }

        // ═══ Cloud & Sync ═══
        bool cloudChanged = Playlist.SyncMode != playlist.SyncMode
                            || !string.Equals(Playlist.YoutubeId, playlist.YoutubeId, StringComparison.Ordinal)
                            || Playlist.IsCloudUnavailable != playlist.IsCloudUnavailable;
        if (cloudChanged)
        {
            Playlist.SyncMode = playlist.SyncMode;
            Playlist.YoutubeId = playlist.YoutubeId;
            Playlist.IsCloudUnavailable = playlist.IsCloudUnavailable;
            ApplyCloudState(playlist);
        }

        DeleteCommand.NotifyCanExecuteChanged();
        CopyLinkCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Sets all ownership/visibility/author properties from the model.
    /// </summary>
    private void ApplyOwnershipState(Core.Models.Playlist playlist)
    {
        ShowAuthor = !string.IsNullOrEmpty(playlist.Author);
        IsPrivate = playlist.Visibility == PlaylistVisibility.Private;
        IsUnlisted = playlist.Visibility == PlaylistVisibility.Unlisted;

        RefreshAuthorTexts();
    }

    /// <summary>
    /// Sets all cloud/sync properties from the model.
    /// </summary>
    private void ApplyCloudState(Core.Models.Playlist playlist)
    {
        HasCloudSource = playlist.HasCloudLink
                         || (IsLikedPlaylist && _auth.IsAuthenticated);

        IsTwoWaySynced = playlist.SyncMode == PlaylistSyncMode.TwoWaySync
                         || (IsLikedPlaylist && _auth.IsAuthenticated);
    }

    /// <summary>
    /// Recalculates localized author strings.
    /// </summary>
    private void RefreshAuthorTexts()
    {
        var author = Author;
        bool hasAuthor = !string.IsNullOrEmpty(author);

        AuthorDisplayText = hasAuthor
            ? string.Format(LocalizationService.Instance["Playlist_ByAuthor"], author)
            : null;

        AuthorTooltip = hasAuthor
            ? string.Format(LocalizationService.Instance["Playlist_Foreign_Tooltip"], author)
            : null;
    }

    /// <summary>
    /// Formats the track count with proper localization pluralization.
    /// </summary>
    private static string FormatTrackCount(int count)
    {
        if (count == 0) return LocalizationService.Instance["Playlist_Empty"];
        return LocalizationService.Instance.GetPlural("Playlist_TracksCount", count);
    }

    /// <summary>
    /// Transitions the card to a visible state (staggered entrance animation).
    /// </summary>
    public void Show()
    {
        if (!_isDisposed) IsVisible = true;
    }

    #endregion

    #region Dispose

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _isDisposed = true;

            _playerControl.PlaybackPurityChanged -= OnPlaybackPurityChanged;
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
        }
        base.Dispose(disposing);
    }

    #endregion
}