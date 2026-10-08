namespace LMP.Core.Exceptions;

/// <summary>
/// Базовое исключение для сбоев при работе с API Яндекс Музыки.
/// </summary>
public class YandexMusicException : Exception
{
    public YandexMusicException(string message) : base(message)
    {
    }

    public YandexMusicException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Исключение недействительного или просроченного OAuth-токена.
/// </summary>
public sealed class YandexAuthException : YandexMusicException
{
    public YandexAuthException(string message = "Yandex Music token is invalid or has expired.")
        : base(message)
    {
    }
}
