using System.Collections.Concurrent;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Абсолютный базовый класс для дешифраторов YouTube, реализующий общую логику 
/// двухуровневого кэширования и защиты от повторной дешифрации (Idempotency).
/// </summary>
public abstract class BaseYoutubeDecryptor : IYoutubeDecryptor, IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Persistent QuickJS управляющий сервис.
    /// </summary>
    protected readonly JsDecryptionService JsService;

    /// <summary>
    /// Кэш дешифрованных значений (string -> string) на диске и в памяти.
    /// </summary>
    protected readonly DecryptorCache Cache;

    /// <summary>
    /// Локальный кэш сессии для предотвращения повторной обработки уже расшифрованных токенов.
    /// </summary>
    protected readonly ConcurrentDictionary<string, byte> DecryptedTokens = new(StringComparer.Ordinal);

    /// <summary>
    /// Менеджер контекста плеера.
    /// </summary>
    public PlayerContextManager PlayerManager => JsService.PlayerManager;

    /// <summary>
    /// Указывает, инициализирован ли внутренний нативный контекст QuickJS.
    /// </summary>
    public bool IsInitialized => JsService.IsInitialized;

    /// <summary>
    /// Гарантирует фоновую инициализацию нативного контекста QuickJS (из кэша байткода или через AST-препроцессинг).
    /// </summary>
    public ValueTask EnsureInitializedAsync(CancellationToken ct = default) =>
        JsService.EnsureInitializedAsync(ct);

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="BaseYoutubeDecryptor"/>.
    /// </summary>
    protected BaseYoutubeDecryptor(JsDecryptionService jsService, string cacheFilePath, int maxMemory, int maxDisk)
    {
        JsService = jsService;
        Cache = new DecryptorCache(cacheFilePath, maxMemory, maxDisk);
    }

    /// <inheritdoc />
    public virtual void InvalidateCache()
    {
        DecryptedTokens.Clear();
        Cache.Clear();
    }

    /// <inheritdoc />
    public Task FlushCacheAsync() => Cache.SaveAsync();

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Освобождает неуправляемые (и управляемые) ресурсы.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            DecryptedTokens.Clear();
            try
            {
                FlushCacheAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Подавление ошибок при закрытии пулов приложения
            }
        }

        _disposed = true;
    }
}