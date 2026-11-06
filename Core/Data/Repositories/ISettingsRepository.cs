using System.Diagnostics.CodeAnalysis;

namespace LMP.Core.Data.Repositories;

/// <summary>
/// Interface for the application settings store.
/// </summary>
public interface ISettingsRepository
{
    /// <summary>
    /// Asynchronously retrieves a deserialized setting value by key.
    /// </summary>
    /// <typeparam name="T">Setting model type.</typeparam>
    /// <param name="key">Unique string key of the setting.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>Model instance, or <c>null</c> if the key is missing.</returns>
    /// <remarks>
    /// The <see cref="DynamicallyAccessedMembersAttribute"/> annotation must match the implementation (IL2095):
    /// On first access, MemoryPack looks for a static formatter registration method on <typeparamref name="T"/>.
    /// </remarks>
    Task<T?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        string key,
        CancellationToken ct = default) where T : class;

    /// <summary>
    /// Retrieves a setting or returns a default value.
    /// </summary>
    /// <typeparam name="T">Setting model type.</typeparam>
    /// <param name="key">Unique string key of the setting.</param>
    /// <param name="defaultValue">Default value.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>Deserialized instance or the default value.</returns>
    /// <remarks>The type is passed through to <see cref="GetAsync{T}"/>, so it carries the same trimming annotation.</remarks>
    Task<T> GetOrDefaultAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        string key,
        T defaultValue,
        CancellationToken ct = default) where T : class;

    /// <summary>
    /// Asynchronously saves a serialized setting to the database.
    /// </summary>
    /// <typeparam name="T">Setting model type.</typeparam>
    /// <param name="key">Unique string key of the setting.</param>
    /// <param name="value">Model instance to save.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task SetAsync<T>(
        string key,
        T value,
        CancellationToken ct = default);

    /// <summary>
    /// Synchronously saves a setting to the database.
    /// Used during application shutdown path to avoid deadlocks.
    /// </summary>
    void Set<T>(
        string key,
        T value);
}