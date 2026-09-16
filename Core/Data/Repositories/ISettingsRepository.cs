using System.Text.Json.Serialization.Metadata;

namespace LMP.Core.Data.Repositories;

/// <summary>
/// Интерфейс хранилища настроек приложения.
/// </summary>
public interface ISettingsRepository
{
    /// <summary>
    /// Асинхронно извлекает десериализованное значение настройки по ключу.
    /// </summary>
    /// <typeparam name="T">Тип модели настройки.</typeparam>
    /// <param name="key">Уникальный строковый ключ настройки.</param>
    /// <param name="typeInfo">Метаданные Source-Generated JSON сериализатора.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Экземпляр модели или <c>null</c>, если ключ отсутствует.</returns>
    Task<T?> GetAsync<T>(
        string key,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default) where T : class;

    /// <summary>
    /// Извлекает настройку или возвращает значение по умолчанию.
    /// </summary>
    /// <typeparam name="T">Тип модели настройки.</typeparam>
    /// <param name="key">Уникальный строковый ключ настройки.</param>
    /// <param name="defaultValue">Значение по умолчанию.</param>
    /// <param name="typeInfo">Метаданные Source-Generated JSON сериализатора.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Десериализованный экземпляр или значение по умолчанию.</returns>
    Task<T> GetOrDefaultAsync<T>(
        string key,
        T defaultValue,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default) where T : class;

    /// <summary>
    /// Асинхронно сохраняет сериализованную настройку в базу данных.
    /// </summary>
    /// <typeparam name="T">Тип модели настройки.</typeparam>
    /// <param name="key">Уникальный строковый ключ настройки.</param>
    /// <param name="value">Экземпляр модели для сохранения.</param>
    /// <param name="typeInfo">Метаданные Source-Generated JSON сериализатора.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task SetAsync<T>(
        string key,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default);

    /// <summary>
    /// Синхронно сохраняет настройку в базу данных.
    /// Используется при завершении работы приложения (shutdown path) во избежание deadlock.
    /// </summary>
    void Set<T>(
        string key,
        T value,
        JsonTypeInfo<T> typeInfo);
}
