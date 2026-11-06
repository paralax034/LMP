using System.Net;

namespace LMP.Core.Services;

/// <summary>
/// Long-lived HTTP server (singleton) for receiving cookies from the browser extension.
/// The listener starts lazily on the first call to <see cref="WaitForCookiesAsync"/>
/// and runs until <see cref="Dispose"/>.
/// Multiple calls to <see cref="WaitForCookiesAsync"/> safely replace each other.
/// </summary>
public sealed class LocalAuthServer : IDisposable
{
    private const int Port = 40340;
    private static readonly byte[] PongResponse = "pong"u8.ToArray();
    private static string Prefix => $"http://127.0.0.1:{Port}/api/";

    private static LocalAuthServer? _createdInstance;

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _serverCts = new();
    private readonly Lock _lock = new();

    /// <summary>Pending call to WaitForCookiesAsync waiting for a result.</summary>
    private TaskCompletionSource<string?>? _pendingTcs;

    private bool _isStarted;
    private bool _isDisposed;

    /// <summary>
    /// Initializes the server object without starting the listener.
    /// The actual start occurs lazily on the first cookie wait.
    /// </summary>
    public LocalAuthServer()
    {
        _listener.Prefixes.Add(Prefix);
        _createdInstance = this;
    }

    /// <summary>
    /// Disposes a previously created server instance if it was initialized.
    /// Does not trigger service creation via DI.
    /// </summary>
    public static void DisposeIfCreated()
    {
        _createdInstance?.Dispose();
    }

    /// <summary>
    /// Waits for cookies from the extension. On repeated calls, the previous wait is canceled.
    /// Canceling <paramref name="ct"/> cancels the wait without stopping the server.
    /// </summary>
    /// <param name="ct">Cancellation token for the wait operation.</param>
    /// <returns>A cookie string or <c>null</c> if the wait was canceled.</returns>
    public Task<string?> WaitForCookiesAsync(CancellationToken ct)
    {
        if (!EnsureStarted())
            return Task.FromResult<string?>(null);

        TaskCompletionSource<string?> tcs;

        lock (_lock)
        {
            if (_isDisposed)
                return Task.FromResult<string?>(null);

            _pendingTcs?.TrySetResult(null);

            tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingTcs = tcs;
        }

        return WaitForCookiesCoreAsync(tcs, ct);
    }

    /// <summary>
    /// Lazily starts the listener and background loop on first actual use.
    /// </summary>
    /// <returns><c>true</c> if the server is running or was already running.</returns>
    private bool EnsureStarted()
    {
        lock (_lock)
        {
            if (_isDisposed)
                return false;

            if (_isStarted)
                return true;

            try
            {
                _listener.Start();
                _isStarted = true;
                Log.Debug($"[LocalAuthServer] Listening on {Prefix}");
            }
            catch (HttpListenerException ex)
            {
                Log.Error($"[LocalAuthServer] Failed to start (port busy?): {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"[LocalAuthServer] Unexpected start error: {ex.Message}");
                return false;
            }
        }

        _ = RunLoopAsync(_serverCts.Token);
        return true;
    }

    /// <summary>
    /// Binds a user cancellation token to a specific cookie wait operation.
    /// </summary>
    private async Task<string?> WaitForCookiesCoreAsync(TaskCompletionSource<string?> tcs, CancellationToken ct)
    {
        using var registration = ct.Register(static state =>
        {
            var waiter = (CookieWaiterState)state!;
            waiter.Server.CancelPendingWait(waiter.Tcs);
        }, new CookieWaiterState(this, tcs));

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the cookie wait for the specified waiter if it is still active.
    /// </summary>
    private void CancelPendingWait(TaskCompletionSource<string?> tcs)
    {
        lock (_lock)
        {
            if (_pendingTcs == tcs)
            {
                _pendingTcs = null;
                tcs.TrySetResult(null);
            }
        }
    }

    /// <summary>
    /// Main HTTP request processing loop. Runs until <see cref="_serverCts"/> is canceled.
    /// </summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException ex)
            {
                Log.Warn($"[LocalAuthServer] GetContext error: {ex.Message}");
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = HandleContextAsync(context, ct);
        }

        Log.Debug("[LocalAuthServer] Loop stopped.");
    }

    /// <summary>
    /// Processes a single HTTP request.
    /// </summary>
    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            // CORS
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Extension-Version");

            var path = request.Url?.AbsolutePath ?? "";

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                return;
            }

            if (request.HttpMethod == "GET" &&
                path.Equals("/api/ping", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 200;
                response.ContentType = "text/plain";
                response.ContentLength64 = PongResponse.Length;
                await response.OutputStream.WriteAsync(PongResponse, ct).ConfigureAwait(false);
                return;
            }

            if (request.HttpMethod == "POST" &&
                path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(request.InputStream);
                var cookies = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                response.StatusCode = 200;

                lock (_lock)
                {
                    if (_pendingTcs != null)
                    {
                        _pendingTcs.TrySetResult(cookies);
                        _pendingTcs = null;
                    }
                    else
                    {
                        Log.Warn("[LocalAuthServer] Received cookies but no one is waiting.");
                    }
                }

                return;
            }

            response.StatusCode = 405;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"[LocalAuthServer] HandleContext error: {ex.Message}");
            try { response.StatusCode = 500; } catch { }
        }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
        }

        _serverCts.Cancel();

        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }

        lock (_lock)
        {
            _pendingTcs?.TrySetResult(null);
            _pendingTcs = null;
        }

        _serverCts.Dispose();
        Log.Debug("[LocalAuthServer] Disposed.");
    }

    /// <summary>
    /// Allocation-free struct for passing cancellation state.
    /// </summary>
    private readonly record struct CookieWaiterState(
        LocalAuthServer Server,
        TaskCompletionSource<string?> Tcs);
}