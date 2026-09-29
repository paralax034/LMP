using System.Windows.Input;
using Avalonia.Media;

namespace LMP.UI.Features.Shared;

/// <summary>
/// Представляет интерактивный элемент плейлиста внутри каскадного подменю добавления треков.
/// </summary>
public sealed partial class PlaylistMenuItemViewModel : ObservableObject
{
    private static StreamGeometry? _checkGeometry;
    private static StreamGeometry? _minusGeometry;
    private static StreamGeometry? _plusGeometry;

    private static StreamGeometry? CheckGeometry =>
        _checkGeometry ??= ResolveGeometry("Icon.Check");

    private static StreamGeometry? MinusGeometry =>
        _minusGeometry ??= ResolveGeometry("Icon.Minus");

    private static StreamGeometry? PlusGeometry =>
        _plusGeometry ??= ResolveGeometry("Icon.Plus");

    private static StreamGeometry? ResolveGeometry(string key) =>
        Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var res) == true
            ? res as StreamGeometry
            : null;

    public string PlaylistId { get; }
    public string Name { get; }
    public bool IsCreateAction { get; }
    public bool IsSeparator => Name == "-";
    public bool StaysOpenOnClick => !IsCreateAction && !IsSeparator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconGeometry))]
    [NotifyPropertyChangedFor(nameof(IsActiveState))]
    public partial PlaylistMembershipState State { get; set; }

    [ObservableProperty]
    public partial string? CountText { get; set; }

    public StreamGeometry? IconGeometry => IsCreateAction
        ? PlusGeometry
        : State switch
        {
            PlaylistMembershipState.All => CheckGeometry,
            PlaylistMembershipState.Indeterminate => MinusGeometry,
            _ => null
        };

    public bool IsActiveState => State is PlaylistMembershipState.All or PlaylistMembershipState.Indeterminate;

    public ICommand Command { get; }

    public PlaylistMenuItemViewModel(
        string playlistId,
        string name,
        PlaylistMembershipState state,
        string? countText,
        Func<PlaylistMenuItemViewModel, Task> onToggle,
        bool isCreateAction = false)
    {
        PlaylistId = playlistId;
        Name = name;
        State = state;
        CountText = countText;
        IsCreateAction = isCreateAction;

        Command = new TrackAsyncCommand(() => onToggle(this));
    }

    public static PlaylistMenuItemViewModel CreateSeparator() =>
        new(string.Empty, "-", PlaylistMembershipState.None, null, _ => Task.CompletedTask);
}