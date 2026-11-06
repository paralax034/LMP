namespace LMP.Core.Youtube.Bridge.PoToken;

/// <summary>
/// High-level provider for content-bound PoToken.
/// Coordinates token generation via <see cref="BotGuardService"/>.
/// </summary>
public sealed class PoTokenProvider : IDisposable
{
    private readonly Func<HttpClient> _httpProvider;
    private readonly Lock _sync = new();
    private BotGuardService _botGuard;
    private bool _disposed;

    /// <summary>
    /// Indicates whether the internal BotGuard service is ready for instant fast-minting without a full initialization cycle.
    /// </summary>
    public bool HasActiveContext
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && _botGuard.HasActiveContext;
            }
        }
    }

    /// <param name="httpProvider">HTTP client factory for WAA/gstatic requests. Does not require YouTube cookies.</param>
    public PoTokenProvider(Func<HttpClient> httpProvider)
    {
        _httpProvider = httpProvider ?? throw new ArgumentNullException(nameof(httpProvider));
        _botGuard = new BotGuardService(_httpProvider());
    }

    /// <summary>
    /// Warms up the BotGuard context and acquires an IntegrityToken (TTL 12 hours) in the background,
    /// ensuring the first track in the session uses fast minting (~2 ms) instead of a cold pipeline (~900 ms).
    /// </summary>
    /// <param name="visitorData">Current session VisitorData for initial setup.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WarmupAsync(string? visitorData, CancellationToken ct = default)
    {
        BotGuardService service;
        lock (_sync)
        {
            if (_disposed) return;
            service = _botGuard;
        }

        if (service.HasActiveContext)
            return;

        string seedId = string.IsNullOrWhiteSpace(visitorData) ? "dQw4w9WgXcQ" : visitorData;
        _ = await service.MintForVideoAsync(seedId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Content-bound PoToken for the <c>&amp;pot=</c> videoplayback URL parameter.
    /// Reuses the cached BotGuard context — minting step only (~2ms).
    /// The full pipeline (~1s) is only executed when the IntegrityToken TTL expires.
    /// </summary>
    /// <param name="videoId">YouTube video ID — used as the content binding identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string?> GetContentTokenAsync(string videoId, CancellationToken ct = default)
    {
        BotGuardService service;
        lock (_sync)
        {
            if (_disposed) return null;
            service = _botGuard;
        }

        Log.Debug($"[PoTokenProvider] Content-bound PoToken for {Truncate(videoId)}");
        var token = await service.MintForVideoAsync(videoId, ct).ConfigureAwait(false);

        if (token is not null && token.Length != 116 && token.Length != 188)
        {
            Log.Warn($"[PoTokenProvider] Token must be 116 or 188 ({token.Length}ch) — " +
                     "BotGuard environment likely failing checks, discarding");
            return null;
        }

        return token;
    }

    /// <summary>
    /// Invalidates the in-memory cache and resets the initialization flag.
    /// Call this when switching accounts / VisitorData.
    /// </summary>
    public void Invalidate()
    {
        lock (_sync)
        {
            if (_disposed) return;
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