using System.Text.Json.Serialization;
using MemoryPack;

namespace LMP.Core.Models;

/// <summary>
/// Настройки темы приложения.
/// Все цвета хранятся в HEX-формате (#RRGGBB или #AARRGGBB).
/// </summary>
[MemoryPackable]
public sealed partial class ThemeSettings
{
    /// <summary>Имя темы для отображения</summary>
    public string Name { get; set; } = "Paralax Purple";

    // BACKGROUNDS - Фоновые цвета

    /// <summary>Основной фон окна</summary>
    public string BgPrimary { get; set; } = "#0F0B15";

    /// <summary>Фон карточек и сайдбара</summary>
    public string BgSecondary { get; set; } = "#1A1625";

    /// <summary>Фон диалогов и меню</summary>
    public string BgElevated { get; set; } = "#252033";

    /// <summary>Разделители и границы</summary>
    public string BgHighlight { get; set; } = "#322A45";

    /// <summary>Hover-состояние</summary>
    public string BgHover { get; set; } = "#3E3456";

    // SKELETON / LOADING - Цвета загрузки

    /// <summary>Скелетон светлый</summary>
    public string BgSkeleton { get; set; } = "#2A2438";

    /// <summary>Скелетон темный</summary>
    public string BgSkeletonDeep { get; set; } = "#15121C";

    /// <summary>Оверлей (полупрозрачный)</summary>
    public string BgOverlay { get; set; } = "#CC0A080F";

    // ACCENT - Акцентные цвета бренда

    /// <summary>Основной акцентный цвет (кнопки, ссылки)</summary>
    public string AccentColor { get; set; } = "#8A2BE2";

    /// <summary>Акцент при наведении</summary>
    public string AccentHover { get; set; } = "#A560F0";

    // SEMANTIC - Системные цвета

    /// <summary>Цвет ошибки</summary>
    public string SystemError { get; set; } = "#FF5555";

    /// <summary>Фон ошибки</summary>
    public string SystemErrorBg { get; set; } = "#331010";

    /// <summary>Информационный цвет</summary>
    public string SystemInfoBlue { get; set; } = "#8BE9FD";

    /// <summary>Предупреждение</summary>
    public string SystemWarnOrange { get; set; } = "#FFB86C";

    // TEXT - Цвета текста

    /// <summary>Основной текст</summary>
    public string TextPrimary { get; set; } = "#F8F8F2";

    /// <summary>Вторичный текст (подзаголовки)</summary>
    public string TextSecondary { get; set; } = "#BFB6D3";

    /// <summary>Приглушенный текст</summary>
    public string TextMuted { get; set; } = "#6272A4";

    /// <summary>Темный текст (на светлом фоне)</summary>
    public string TextDark { get; set; } = "#0F0B15";

    // SERIALIZATION

    [JsonIgnore]
    [MemoryPackIgnore]
    public bool IsBuiltIn { get; init; }

    public override string ToString() => Name;
}