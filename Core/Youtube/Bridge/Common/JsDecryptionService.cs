namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Singleton service for managing persistent QuickJS context.
/// <para>
/// <b>Architecture:</b> single native handle for the entire application lifetime.
/// All decryptors (<see cref="NToken.NTokenDecryptor"/>,
/// <see cref="SigCipher.SigCipherDecryptor"/>) share a single context —
/// the script is compiled exactly once instead of once per call.
/// </para>
/// <para>
/// <b>Thread safety:</b> QuickJS is not thread-safe. All calls to the native
/// layer are serialized via <c>_callLock</c> (SemaphoreSlim 1,1).
/// Initialization is protected by a separate <c>_initLock</c>.
/// </para>
/// <para>
/// <b>Lifecycle:</b>
/// <list type="bullet">
///   <item>Lazy init on first call — does not block application startup.</item>
///   <item>On player version change — <see cref="ReinitializeAsync"/> creates a new handle.</item>
///   <item>Dispose — destroys native handle, releases 250 KB QuickJS runtime.</item>
/// </list>
/// </para>
/// </summary>
public sealed class JsDecryptionService : IDisposable
{
    private readonly PlayerContextManager _playerManager;

    private IntPtr _handle = IntPtr.Zero;
    private volatile string? _currentPlayerVersion;
    private volatile bool _initialized;
    private volatile bool _disposed;

    /// <summary>Serializes all native calls: QuickJS is not thread-safe.</summary>
    private readonly SemaphoreSlim _callLock = new(1, 1);

    /// <summary>Protects against concurrent initialization (double-init race).</summary>
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>Player context manager. Used for Invalidate during 403-recovery.</summary>
    public PlayerContextManager PlayerManager => _playerManager;

    /// <summary>
    /// Indicates whether the native QuickJS context is initialized and ready for instant calls (~0.1 ms).
    /// </summary>
    public bool IsInitialized => _initialized && _handle != IntPtr.Zero && !_disposed;

    /// <param name="playerManager">
    /// Player context manager — the source of the preprocessed script.
    /// </param>
    public JsDecryptionService(PlayerContextManager playerManager)
    {
        _playerManager = playerManager;
    }

    //  Public API 

    /// <summary>
    /// Ensures that the native context is initialized.
    /// <para>
    /// Idempotent: safe to call from multiple threads concurrently.
    /// Heavy work (AST preprocessing) is executed on the ThreadPool.
    /// </para>
    /// </summary>
    public async ValueTask EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized && _handle != IntPtr.Zero) return;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized && _handle != IntPtr.Zero) return;

            var context = await _playerManager.GetOrLoadAsync(ct).ConfigureAwait(false);

            // Pass context entirely. AST parsing and text reading will NOT execute
            // if CreateHandleAsync successfully initializes the context from bytecode.
            await CreateHandleAsync(context, ct).ConfigureAwait(false);

            _initialized = true;
            context.ReleaseRawScripts();

            Log.Info($"[JsDecryptionService] Persistent context initialized (player: {context.Version})");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Invokes <c>globalThis._result.{functionName}(argument)</c>
    /// in persistent context without re-evaluating the script.
    /// <para>
    /// Hot path: ~0.1 ms per call. Fully asynchronous — does not block the UI.
    /// </para>
    /// </summary>
    /// <param name="functionName">Function name: <c>"n"</c>, <c>"sig"</c>, etc.</param>
    /// <param name="argument">Input string for decryption.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Decryption result or <c>null</c> on error.</returns>
    public async ValueTask<string?> CallAsync(
        string functionName,
        string argument,
        CancellationToken ct = default)
    {
        if (_disposed) return null;

        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await CheckAndReinitializeIfNeededAsync(ct).ConfigureAwait(false);

        await _callLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = QuickJsNative.CallFunction(_handle, functionName, argument);

            if (result == null)
            {
                var nativeError = QuickJsNative.GetLastError(_handle);
                Log.Warn(
                    $"[JsDecryptionService] {functionName}({argument.Truncate()}) → null. " +
                    $"Native last error: {nativeError ?? "(none)"}");
            }

            return result;
        }
        finally
        {
            _callLock.Release();
        }
    }

    /// <summary>
    /// Force-reinitializes the native context with a new player.
    /// Called on YouTube version rotation or during 403 error recovery.
    /// </summary>
    private async ValueTask ReinitializeAsync(CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _initialized = false;

            // Retrieve the current player context (from disk cache or network)
            var context = await _playerManager.GetOrLoadAsync(ct).ConfigureAwait(false);

            // Simply pass the context! CreateHandleAsync will resolve it internally:
            // if bytecode exists on disk — it instantly initializes the context from it (in ~5 ms),
            // without reading preprocessed.js or running the heavy AST parser.
            await CreateHandleAsync(context, ct).ConfigureAwait(false);

            _initialized = true;
            context.ReleaseRawScripts();

            Log.Info($"[JsDecryptionService] Context reinitialized (player: {context.Version})");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Resets the initialization flag.
    /// The next call will recreate the QuickJS context from the cached script.
    /// <para>
    /// On 403-recovery, the context is recreated from disk cache (~3 ms)
    /// rather than network (~2.5 s download + ~1.5 s AST parse).
    /// </para>
    /// </summary>
    public void Invalidate()
    {
        _initialized = false;
        _currentPlayerVersion = null;
        Log.Info("[JsDecryptionService] Invalidated — will reinitialize on next call");
    }

    /// <summary>
    /// Called when a broken solver is detected (n-token → null).
    /// Resets in-memory state and physically removes all disk artifacts
    /// of the current player version, forcing a full base.js reload on the next call.
    /// </summary>
    /// <remarks>
    /// Synchronous method: file operations complete immediately without fire-and-forget
    /// to guarantee absence of bytecode prior to the next <see cref="EnsureInitializedAsync"/>.
    /// </remarks>
    public void InvalidatePlayerContextAndBytecode()
    {
        var version = _currentPlayerVersion;

        _initialized = false;
        _currentPlayerVersion = null;

        // Soft RAM context reset; ClearDiskCache is executed separately
        // to avoid relying on _current (which may be null after a previous InvalidateContext).
        _playerManager.InvalidateContext();

        if (!string.IsNullOrEmpty(version))
        {
            PlayerContext.ClearDiskCache(version);
            Log.Warn($"[JsDecryptionService] Broken n-solver — purged all disk cache for player {version}");
        }
        else
        {
            Log.Warn("[JsDecryptionService] Broken n-solver — version unknown, RAM context cleared");
        }
    }

    //  Private helpers 

    /// <summary>
    /// Creates a new native handle. Tries to load from bytecode cache (fast path);
    /// on failure, parses text, extracts bytecode from the same context, and asynchronously saves it.
    /// Atomically replaces the old handle with the new one.
    /// </summary>
    private async ValueTask CreateHandleAsync(PlayerContext context, CancellationToken ct)
    {
        IntPtr newHandle = IntPtr.Zero;
        var bytecodePath = PlayerContext.GetBytecodeCachePath(context.Version);

        // 1. Fast path: Try loading from bytecode
        if (File.Exists(bytecodePath))
        {
            try
            {
                var bytecode = await File.ReadAllBytesAsync(bytecodePath, ct).ConfigureAwait(false);
                newHandle = await Task.Run(() => QuickJsNative.CreateContextFromBytecode(bytecode), ct).ConfigureAwait(false);

                if (newHandle != IntPtr.Zero)
                {
                    Log.Info($"[JsDecryptionService] Context loaded from bytecode ({bytecode.Length / 1024}KB) — AST preprocessing skipped");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[JsDecryptionService] Bytecode load failed: {ex.Message}");
            }
        }

        // 2. Slow path: Bytecode failure. Lazily read JS from disk (or compile) and recreate
        bool performedHeavyAstParse = false;
        if (newHandle == IntPtr.Zero)
        {
            performedHeavyAstParse = true;

            // GetOrPrepareScript will be called to lazily read preprocessed.js only now!
            var script = await Task.Run(
                () => context.GetOrPrepareScript(
                    () => YoutubeAstSolver.PreprocessPlayer(context.BaseJs)),
                ct).ConfigureAwait(false);

            var (handle, bytecode) = await Task.Run(() => QuickJsNative.CreateContextWithBytecode(script), ct).ConfigureAwait(false);
            newHandle = handle;

            if (bytecode is { Length: > 0 })
            {
                _ = SaveBytecodeCacheAsync(bytecodePath, bytecode);
            }
        }

        if (performedHeavyAstParse)
        {
            context.ReleaseRawScripts();
            MemoryHelpers.SchedulePostHeavyWorkCleanup();
        }

        if (newHandle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"[JsDecryptionService] Failed to create QuickJS context for player {context.Version}");

        await _callLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var oldHandle = _handle;
            _handle = newHandle;
            _currentPlayerVersion = context.Version;

            if (oldHandle != IntPtr.Zero)
                QuickJsNative.DestroyContext(oldHandle);
        }
        finally
        {
            _callLock.Release();
        }
    }

    /// <summary>
    /// Checks if the player version has changed and reinitializes if necessary.
    /// </summary>
    private async ValueTask CheckAndReinitializeIfNeededAsync(CancellationToken ct)
    {
        if (!_initialized || _handle == IntPtr.Zero) return;

        PlayerContext? context;
        try
        {
            context = await _playerManager.GetOrLoadAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (!string.IsNullOrEmpty(_currentPlayerVersion) &&
            !string.Equals(_currentPlayerVersion, context.Version, StringComparison.Ordinal))
        {
            Log.Info($"[JsDecryptionService] Player version changed: " +
                     $"{_currentPlayerVersion} → {context.Version}. Reinitializing...");
            await ReinitializeAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task SaveBytecodeCacheAsync(string bytecodePath, byte[] bytecode)
    {
        try
        {
            await File.WriteAllBytesAsync(bytecodePath, bytecode, CancellationToken.None).ConfigureAwait(false);
            Log.Info($"[JsDecryptionService] Bytecode cached: {bytecode.Length / 1024}KB");
        }
        catch (Exception ex)
        {
            Log.Warn($"[JsDecryptionService] Bytecode save failed: {ex.Message}");
        }
    }

    //  Dispose

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _callLock.Wait(millisecondsTimeout: 2000);
        try
        {
            if (_handle != IntPtr.Zero)
            {
                QuickJsNative.DestroyContext(_handle);
                _handle = IntPtr.Zero;
            }
        }
        finally
        {
            _callLock.Release();
        }

        _callLock.Dispose();
        _initLock.Dispose();

        Log.Info("[JsDecryptionService] Disposed — QuickJS runtime released");
    }
}