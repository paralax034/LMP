using MemoryPack;

namespace LMP.Core.Models;

/// <summary>
/// Представляет состояние авторизации пользователя Google/YouTube.
/// </summary>
[MemoryPackable]
public sealed partial class AuthState
{
    public const string DefaultAuthUser = "0";

    private string _userName = "Guest";

    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// Отображаемое имя пользователя. Автоматически возвращает локализованное имя гостя из ресурсов при отсутствии сессии.
    /// </summary>
    public string UserName
    {
        get => IsAuthenticated ? _userName : LocalizationService.Instance["Auth_Guest"];
        set => _userName = value;
    }

    public string UserEmail { get; set; } = "";
    public string AvatarUrl { get; set; } = "";

    /// <summary>
    /// Идентификатор активного аккаунта YouTube (Gaia ID) для точечной изоляции.
    /// </summary>
    public string ActiveGaiaId { get; set; } = string.Empty;

    /// <summary>
    /// Индекс сессии мульти-авторизации Google активного аккаунта.
    /// По умолчанию пустая строка — это позволяет доверять флагу IsSelected от YouTube 
    /// при первичном входе через расширение.
    /// </summary>
    public string AuthUser { get; set; } = DefaultAuthUser;

    /// <summary>
    /// Кэшированный список каналов пользователя для мгновенного оффлайн-переключения.
    /// </summary>
    public List<YoutubeAccountItem> CachedAccounts { get; set; } = [];

    /// <summary>
    /// Уникальный идентификатор текущего профиля для изоляции данных в БД.
    /// Использует Gaia ID при наличии, иначе email, с безусловным fallback на гостя.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    [MemoryPackIgnore]
    public string DisplayId => !string.IsNullOrEmpty(ActiveGaiaId)
        ? ActiveGaiaId
        : (!string.IsNullOrEmpty(UserEmail) ? UserEmail : "guest");

    public DateTime LastUpdated { get; set; } = DateTime.MinValue;
}