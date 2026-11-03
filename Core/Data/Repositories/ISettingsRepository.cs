using System.Diagnostics.CodeAnalysis;

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
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Экземпляр модели или <c>null</c>, если ключ отсутствует.</returns>
    /// <remarks>
    /// Аннотация <see cref="DynamicallyAccessedMembersAttribute"/> обязана совпадать с реализацией (IL2095):
    /// MemoryPack при первом обращении ищет у <typeparamref name="T"/> статический метод регистрации форматтера.
    /// </remarks>
    Task<T?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        string key,
        CancellationToken ct = default) where T : class;

    /// <summary>
    /// Извлекает настройку или возвращает значение по умолчанию.
    /// </summary>
    /// <typeparam name="T">Тип модели настройки.</typeparam>
    /// <param name="key">Уникальный строковый ключ настройки.</param>
    /// <param name="defaultValue">Значение по умолчанию.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Десериализованный экземпляр или значение по умолчанию.</returns>
    /// <remarks>Тип пробрасывается в <see cref="GetAsync{T}"/>, поэтому несёт ту же аннотацию тримминга.</remarks>
    Task<T> GetOrDefaultAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        string key,
        T defaultValue,
        CancellationToken ct = default) where T : class;

    /// <summary>
    /// Асинхронно сохраняет сериализованную настройку в базу данных.
    /// </summary>
    /// <typeparam name="T">Тип модели настройки.</typeparam>
    /// <param name="key">Уникальный строковый ключ настройки.</param>
    /// <param name="value">Экземпляр модели для сохранения.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task SetAsync<T>(
        string key,
        T value,
        CancellationToken ct = default);

    /// <summary>
    /// Синхронно сохраняет настройку в базу данных.
    /// Используется при завершении работы приложения (shutdown path) во избежание deadlock.
    /// </summary>
    void Set<T>(
        string key,
        T value);
}