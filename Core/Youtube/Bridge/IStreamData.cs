namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Интерфейс метаданных сырого медиапотока YouTube.
/// </summary>
internal interface IStreamData
{
    /// <summary>Идентификатор формата (itag).</summary>
    int? Itag { get; }

    /// <summary>Прямой или дешифрованный URL потока.</summary>
    string? Url { get; }

    /// <summary>Шифрованная подпись (при наличии cipher).</summary>
    string? Signature { get; }

    /// <summary>Имя URL-параметра подписи (обычно sig или s).</summary>
    string? SignatureParameter { get; }

    /// <summary>Размер контента в байтах.</summary>
    long? ContentLength { get; }

    /// <summary>Битрейт потока в bps.</summary>
    long? Bitrate { get; }

    /// <summary>MIME-тип медиапотока.</summary>
    string? MimeType { get; }

    /// <summary>Формат контейнера (webm, mp4, etc).</summary>
    string? Container { get; }

    /// <summary>Строка аудиокодека (opus, mp4a.40.2, etc).</summary>
    string? AudioCodec { get; }

    /// <summary>Количество аудиоканалов (1 = mono, 2 = stereo, 6 = 5.1 surround).</summary>
    int AudioChannels { get; }

    /// <summary>Код языка аудиодорожки.</summary>
    string? AudioLanguageCode { get; }

    /// <summary>Отображаемое имя языка аудиодорожки.</summary>
    string? AudioLanguageName { get; }

    /// <summary>Является ли аудиодорожка языком по умолчанию.</summary>
    bool? IsAudioLanguageDefault { get; }

    /// <summary>Строка видеокодека.</summary>
    string? VideoCodec { get; }

    /// <summary>Метка качества видео (например, 1080p).</summary>
    string? VideoQualityLabel { get; }

    /// <summary>Ширина кадра в пикселях.</summary>
    int? VideoWidth { get; }

    /// <summary>Высота кадра в пикселях.</summary>
    int? VideoHeight { get; }

    /// <summary>Частота кадров видеопотока.</summary>
    int? VideoFramerate { get; }
}