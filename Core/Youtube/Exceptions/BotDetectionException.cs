namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Thrown when an operation is blocked due to YouTube bot detection cooldown.
/// Contains information about the remaining wait time.
/// </summary>
public sealed class BotDetectionException(string message, TimeSpan remaining) : YoutubeExplodeException(message)
{
    /// <summary>
    /// Remaining cooldown duration.
    /// </summary>
    public TimeSpan RemainingCooldown { get; } = remaining;

    /// <summary>
    /// Time when the cooldown expires.
    /// </summary>
    public DateTime CooldownEndsAt { get; } = DateTime.UtcNow + remaining;
}