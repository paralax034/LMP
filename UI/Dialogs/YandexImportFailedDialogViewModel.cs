using System.Collections.ObjectModel;
using System.Text;
using LMP.Core.Models;
using LMP.UI.Services;

namespace LMP.UI.Dialogs;

/// <summary>
/// Модель представления диалога со списком ненайденных треков, копированием и повтором.
/// </summary>
public sealed partial class YandexImportFailedDialogViewModel : ViewModelBase
{
    public ObservableCollection<ExternalTrack> FailedTracks { get; }

    public string FailedCountText =>
        string.Format(LocalizationService.Instance["Import_Yandex_Failed_Title"], FailedTracks.Count);

    public Action<bool>? OnResult { get; set; }

    public IAsyncRelayCommand CopyListCommand { get; }
    public IRelayCommand RetryCommand { get; }
    public IRelayCommand FinishCommand { get; }

    public YandexImportFailedDialogViewModel(IReadOnlyList<ExternalTrack> failedTracks)
    {
        FailedTracks = new ObservableCollection<ExternalTrack>(failedTracks);

        CopyListCommand = new AsyncRelayCommand(CopyFailedListToClipboardAsync);
        RetryCommand = new RelayCommand(() => OnResult?.Invoke(true));
        FinishCommand = new RelayCommand(() => OnResult?.Invoke(false));
    }

    private async Task CopyFailedListToClipboardAsync()
    {
        if (FailedTracks.Count == 0) return;

        var sb = new StringBuilder(FailedTracks.Count * 40);
        foreach (var t in FailedTracks)
        {
            sb.AppendLine($"{t.Artist} - {t.Title}");
        }

        await Helpers.Clipboard.SetTextAsync(sb.ToString().TrimEnd());
        CopyHintService.Instance.Show(
            LocalizationService.Instance["Import_Yandex_Copied_Toast"],
            CopyHintKind.Success);
    }
}
