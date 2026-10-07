using System.Collections.ObjectModel;
using LMP.Core.Models;

namespace LMP.UI.Dialogs;

/// <summary>
/// Модель представления диалога со списком ненайденных треков и кнопкой повторной попытки.
/// </summary>
public sealed partial class YandexImportFailedDialogViewModel : ViewModelBase
{
    public ObservableCollection<ExternalTrack> FailedTracks { get; }

    public string FailedCountText =>
        string.Format(LocalizationService.Instance["Import_Yandex_Failed_Title"], FailedTracks.Count);

    public Action<bool>? OnResult { get; set; }

    public IRelayCommand RetryCommand { get; }
    public IRelayCommand FinishCommand { get; }

    public YandexImportFailedDialogViewModel(IReadOnlyList<ExternalTrack> failedTracks)
    {
        FailedTracks = new ObservableCollection<ExternalTrack>(failedTracks);
        RetryCommand = new RelayCommand(() => OnResult?.Invoke(true));
        FinishCommand = new RelayCommand(() => OnResult?.Invoke(false));
    }
}
