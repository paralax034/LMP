using MemoryPack;

namespace LMP.Core.Models;

[MemoryPackable]
public sealed partial class YoutubeAccountItem
{
    public static LocalizationService L => LocalizationService.Instance;

    /// <summary>
    /// Порядковый номер аккаунта в списке.
    /// </summary>
    public int Index { get; set; }

    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string AvatarUrl { get; set; } = "";

    /// <summary>
    /// Идентификатор Gaia основного аккаунта.
    /// </summary>
    public string GaiaId { get; set; } = "";

    /// <summary>
    /// Тег канала (например, @handle).
    /// </summary>
    public string Handle { get; set; } = "";

    /// <summary>
    /// Возвращает приоритетный ID для отображения (GaiaId для основного профиля или Email).
    /// </summary>
    public string DisplayId => !string.IsNullOrEmpty(GaiaId) ? GaiaId : Email;

    /// <summary>
    /// Индекс сессии мульти-авторизации Google (обычно "0" для основного, "1", "2" для дополнительных).
    /// </summary>
    public string AuthUser { get; set; } = AuthState.DefaultAuthUser;

    public bool IsSelected { get; set; }
}