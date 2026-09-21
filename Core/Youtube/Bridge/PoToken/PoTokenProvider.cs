namespace LMP.Core.Youtube.Bridge.PoToken;

/// <summary>
/// Высокоуровневый провайдер content-bound PoToken.
/// Координирует генерацию токенов через <see cref="BotGuardService"/>.
/// </summary>
public sealed class PoTokenProvider : IDisposable
{
    private readonly Func<HttpClient> _httpProvider;
    private readonly Lock _sync = new();
    private BotGuardService _botGuard;
    private bool _disposed;

    /// <param name="httpProvider">Фабрика HTTP-клиента для WAA/gstatic запросов. Не требует YouTube cookies.</param>
    public PoTokenProvider(Func<HttpClient> httpProvider)
    {
        _httpProvider = httpProvider ?? throw new ArgumentNullException(nameof(httpProvider));
        _botGuard = new BotGuardService(_httpProvider());
    }

    /// <summary>
    /// Content-bound PoToken для параметра <c>&amp;pot=</c> videoplayback URL.
    /// Переиспользует кэшированный BotGuard-контекст — только шаг mint (~50ms).
    /// Полный pipeline (~1s) запускается лишь при истечении IntegrityToken TTL.
    /// </summary>
    /// <param name="videoId">ID видео YouTube — используется как content binding identifier.</param>
    /// <param name="ct">Токен отмены.</param>
    public async Task<string?> GetContentTokenAsync(string videoId, CancellationToken ct = default)
    {
        Log.Debug($"[PoTokenProvider] Content-bound PoToken for {Truncate(videoId)}");
        var token = await _botGuard.MintForVideoAsync(videoId, ct).ConfigureAwait(false);

        if (token is not null && token.Length != 116 && token.Length != 188)
        {
            Log.Warn($"[PoTokenProvider] Token must be 116 or 118 ({token.Length}ch) — " +
                     "BotGuard environment likely failing checks, discarding");
            return null;
        }

        return token;
    }

    /// <summary>
    /// Инвалидирует in-memory кэш и сбрасывает флаг инициализации.
    /// Вызывать при смене аккаунта / VisitorData.
    /// </summary>
    public void Invalidate()
    {
        lock (_sync)
        {
            _botGuard?.Dispose();
            _botGuard = new BotGuardService(_httpProvider());
        }

        Log.Info("[PoTokenProvider] Cache invalidated");
    }

    private static string Truncate(string? s, int len = 12) =>
        s is null ? "null" : s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");

    // IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_sync)
        {
            _botGuard.Dispose();
        }
    }
}