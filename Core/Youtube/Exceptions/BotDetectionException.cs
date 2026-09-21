namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Выбрасывается когда операция заблокирована из-за bot detection cooldown от YouTube.
/// Содержит информацию об оставшемся времени ожидания.
/// </summary>
public sealed class BotDetectionException(string message, TimeSpan remaining) : YoutubeExplodeException(message)
{
    /// <summary>
    /// Оставшееся время cooldown.
    /// </summary>
    public TimeSpan RemainingCooldown { get; } = remaining;

    /// <summary>
    /// Время когда cooldown закончится.
    /// </summary>
    public DateTime CooldownEndsAt { get; } = DateTime.UtcNow + remaining;
}