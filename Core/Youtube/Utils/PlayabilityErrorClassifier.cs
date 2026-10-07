using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Безальтернативный высокопроизводительный классификатор ошибок playability от YouTube.
/// Позволяет распознавать юридические ограничения и извлекать правообладателей.
/// </summary>
public static partial class PlayabilityErrorClassifier
{
    /// <summary>
    /// Анализирует сырое текстовое сообщение ошибки playability и классифицирует его.
    /// </summary>
    /// <param name="errorMessage">Сырой текст ошибки от YouTube API.</param>
    /// <param name="claimant">Имя правообладателя (если найдено).</param>
    /// <returns>Причина недоступности потока.</returns>
    public static StreamUnavailableReason Classify(string errorMessage, out string? claimant)
    {
        claimant = null;
        if (string.IsNullOrEmpty(errorMessage))
            return StreamUnavailableReason.Unknown;

        if (errorMessage.Contains("copyright grounds", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("claimed content by", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("авторских прав", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("жалобы правообладателя", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("заблокировано по жалобе", StringComparison.OrdinalIgnoreCase))
        {
            claimant = ExtractCopyrightHolder(errorMessage);
            return StreamUnavailableReason.CopyrightBlocked;
        }

        if (errorMessage.Contains("not available in your country", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("blocked it in your country", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("not available in your region", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("недоступно в вашей стране", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("недоступно в вашем регионе", StringComparison.OrdinalIgnoreCase))
        {
            return StreamUnavailableReason.RegionBlocked;
        }

        if (errorMessage.Contains("video is private", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("приватное видео", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("личный доступ", StringComparison.OrdinalIgnoreCase))
        {
            return StreamUnavailableReason.Private;
        }

        if (errorMessage.Contains("removed by the uploader", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("video has been removed", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("deleted", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("удалено", StringComparison.OrdinalIgnoreCase))
        {
            return StreamUnavailableReason.Removed;
        }

        if (errorMessage.Contains("age-restricted", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("возрастн", StringComparison.OrdinalIgnoreCase))
        {
            return StreamUnavailableReason.AgeRestricted;
        }

        if (errorMessage.Contains("requires purchase", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("payment required", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("members-only", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("платный контент", StringComparison.OrdinalIgnoreCase))
        {
            return StreamUnavailableReason.PaymentRequired;
        }

        return StreamUnavailableReason.Unknown;
    }

    /// <summary>
    /// Вырезает имя правообладателя из сообщения об ошибке.
    /// </summary>
    public static string ExtractCopyrightHolder(string error)
    {
        if (string.IsNullOrEmpty(error))
            return "YouTube Legal";

        var match = ClaimantRegexEn.Match(error);
        if (match.Success)
            return CleanHolder(match.Groups[1].Value);

        match = ClaimantRegexEnFrom.Match(error);
        if (match.Success)
            return CleanHolder(match.Groups[1].Value);

        match = ClaimantRegexRuMatch.Match(error);
        if (match.Success)
            return CleanHolder(match.Groups[1].Value);

        return "YouTube Legal";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string CleanHolder(string value)
    {
        var span = value.AsSpan().Trim();
        span = span.TrimEnd(".,; ");
        return span.ToString();
    }

    [GeneratedRegex(@"claimed content by\s+([A-Za-z0-9_.\-\s&'()]{2,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ru-RU")]
    private static partial Regex ClaimantRegexEn { get; }

    [GeneratedRegex(@"content from\s+([A-Za-z0-9_.\-\s&'()]{2,40}),\s+who", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ru-RU")]
    private static partial Regex ClaimantRegexEnFrom { get; }

    [GeneratedRegex(@"(?:жалобы правообладателя|по жалобе)\s+([A-Za-z0-9_.\-\s&'()А-Яа-яЁё]{2,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ru-RU")]
    private static partial Regex ClaimantRegexRuMatch { get; }
}