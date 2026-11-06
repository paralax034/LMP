using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LMP.Core.Youtube;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Services;

public partial class CookieAuthService
{
    // Two independent semaphores: auth.json and cookies.txt can be saved in parallel
    private readonly SemaphoreSlim _authSaveSemaphore = new(1, 1);
    private readonly SemaphoreSlim _cookieSaveSemaphore = new(1, 1);
    private CancellationTokenSource? _validateCts;
    private readonly Lock _validateLock = new();

    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _cookieMap = new(32, StringComparer.Ordinal);
    private string _cachedHeaderString = "";
    private readonly string _authDataPath = G.FilePath.AuthData;

    private string? _profileLoadError;
    public bool HasProfileLoadError => _profileLoadError != null;

    public AuthState State { get; private set; } = new();

    public bool IsAuthenticated
    {
        get
        {
            lock (_lock) return _cookieMap.ContainsKey("SAPISID");
        }
    }

    public event Action? OnAuthStateChanged;

    /// <summary>
    /// Initializes the service without file I/O.
    /// File operations are performed in <see cref="InitializeAsync"/>.
    /// </summary>
    public CookieAuthService() { }

    /// <summary>
    /// Performs startup file I/O: loading cookies, profile, and one-time migration.
    /// Must be called from <c>App.axaml.cs</c> before <c>LibraryService.InitializeAsync</c>.
    /// </summary>
    public async Task InitializeAsync()
    {
        await LoadCookiesAsync().ConfigureAwait(false);
        await LoadAuthDataAsync().ConfigureAwait(false);
        UpdateStateAuthStatus();
        await MigrateLegacyAuthFileAsync().ConfigureAwait(false);
    }

    private async Task LoadCookiesAsync()
    {
        try
        {
            var (raw, recovered) = await AtomicFile.ReadTextWithFallbackAsync(G.FilePath.Cookie).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (recovered)
                    Log.Warn("[Auth] Recovered auth cookies from backup (.bak)");

                ParseAndSetCookies(raw);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Failed to load cookies: {ex.Message}");
        }
    }

    private async Task LoadAuthDataAsync()
    {
        try
        {
            var (text, recovered) = await AtomicFile.ReadTextWithFallbackAsync(_authDataPath).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (recovered)
                    Log.Warn("[Auth] Recovered auth profile from backup (.bak)");

                var loadedState = JsonSerializer.Deserialize(text, AppJsonContext.DefaultPretty.AuthState);
                if (loadedState != null)
                {
                    State = loadedState;
                    Log.Info($"[Auth] Profile restored from cache: {State.UserName}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _profileLoadError = ex.Message;
            Log.Error($"[Auth] Failed to load auth data: {ex.Message}");
        }
    }

    private async Task MigrateLegacyAuthFileAsync()
    {
        try
        {
            var legacyPath = Path.Combine(AppContext.BaseDirectory, "auth.json");
            if (!File.Exists(legacyPath)) return;

            if (!File.Exists(_authDataPath) || new FileInfo(_authDataPath).Length == 0)
            {
                // File.Copy: BCL does not provide an async counterpart; Task.Run is redundant for a <10 KB file
                File.Copy(legacyPath, _authDataPath, overwrite: true);
                Log.Info($"[Auth] Migrated auth.json to {_authDataPath}");
                await LoadAuthDataAsync().ConfigureAwait(false);
            }
            File.Delete(legacyPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Auth] Failed to migrate legacy auth.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the user profile and raises the authentication change event
    /// only when the data actually changes.
    /// </summary>
    /// <param name="name">User name.</param>
    /// <param name="email">User email.</param>
    /// <param name="avatarUrl">User avatar URL.</param>
    /// <param name="activeGaiaId">Gaia ID of the active account.</param>
    public void UpdateUserProfile(string name, string email, string avatarUrl, string activeGaiaId)
    {
        bool currentAuth = IsAuthenticated;

        bool previousAuth = State.IsAuthenticated;
        string previousUserName = previousAuth ? State.UserName : string.Empty;
        string previousEmail = State.UserEmail;
        string previousAvatarUrl = State.AvatarUrl;
        string previousGaiaId = State.ActiveGaiaId;

        State.UserName = name;
        State.UserEmail = email;
        State.AvatarUrl = avatarUrl;
        State.LastUpdated = DateTime.UtcNow;
        State.IsAuthenticated = currentAuth;
        State.ActiveGaiaId = activeGaiaId;

        SaveAuthData();

        bool changed =
            previousAuth != currentAuth ||
            !string.Equals(previousUserName, name, StringComparison.Ordinal) ||
            !string.Equals(previousEmail, email, StringComparison.Ordinal) ||
            !string.Equals(previousAvatarUrl, avatarUrl, StringComparison.Ordinal) ||
            !string.Equals(previousGaiaId, activeGaiaId, StringComparison.Ordinal);

        if (changed)
            OnAuthStateChanged?.Invoke();
    }

    public void UpdateCachedAccounts(List<YoutubeAccountItem> accounts)
    {
        lock (_lock)
        {
            State.CachedAccounts = accounts;
        }
        SaveAuthData();
    }

    private void UpdateStateAuthStatus()
    {
        bool currentAuth = IsAuthenticated;
        if (State.IsAuthenticated != currentAuth)
        {
            State.IsAuthenticated = currentAuth;
            if (!HasProfileLoadError) SaveAuthData();
        }
    }

    /// <summary>
    /// Universal text extractor supporting both flat simpleText format
    /// and complex structures with a runs array.
    /// </summary>
    private static string? GetStringValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined) return null;

        // 1. Check for direct simpleText
        if (element.TryGetProperty("simpleText", out var simpleTextProp))
        {
            return simpleTextProp.GetString();
        }

        // 2. Check for formatted runs array
        if (element.TryGetProperty("runs", out var runsProp) && runsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var run in runsProp.EnumerateArray())
            {
                if (run.TryGetProperty("text", out var textProp))
                {
                    return textProp.GetString();
                }
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    /// <summary>
    /// Validates the current session on YouTube servers.
    /// Parses a single switcher response to simultaneously extract the current profile and the list of all multi-account channels.
    /// Automatically terminates the session (Logout) upon receiving a confirmed refusal from Google to avoid infinite 401 errors.
    /// </summary>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A tuple containing session validity flag, error string, and network error flag in case of failure.</returns>
    public async Task<(bool IsValid, string? Error, bool IsNetworkError)> ValidateSessionAsync(CancellationToken ct = default)
    {
        if (!IsAuthenticated)
            return (false, "Not authenticated", false);

        try
        {
            var cookiesHeader = GetCookieHeader();
            var sapisid = GetCookieValue("SAPISID");

            var mainOrigin = "https://www.youtube.com";
            var authHeader = YoutubeHttpHandler.GetAuthHeader(sapisid, mainOrigin);

            // Use the standard GET switcher endpoint
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/getAccountSwitcherEndpoint");
            request.Headers.UserAgent.ParseAdd(YoutubeClientUtils.UaWeb);
            request.Headers.Add("Cookie", cookiesHeader);
            request.Headers.Add("Origin", mainOrigin);
            request.Headers.Add("X-Origin", mainOrigin);
            request.Headers.Add("Referer", mainOrigin + "/");

            if (!string.IsNullOrEmpty(authHeader))
                request.Headers.Add("Authorization", authHeader);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            using var response = await Audio.Http.SharedHttpClient.Instance.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);

            // If an explicit 401 is returned, the session is guaranteed dead. Force logout from account
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Log.Warn("[Auth] 401 Unauthorized received during session validation. Session expired. Automatically logging out...");
                Logout();
                return (false, "Unauthorized", false);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                    UpdateCookies(setCookies);

                var rawJson = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                // XSSI defense: Strip the )]}' prefix before parsing JSON
                int jsonStart = rawJson.IndexOf('{');
                if (jsonStart >= 0)
                {
                    rawJson = rawJson.Substring(jsonStart);
                }

                using var jsonDoc = JsonDocument.Parse(rawJson);

                var results = new List<YoutubeAccountItem>();
                bool hasActiveAccount = false;
                string? activeName = null, activeEmail = null, activeAvatar = null, activeGaiaId = null;
                string? activeHandle = null;

                // getAccountSwitcherEndpoint wraps the entire InnerTube response in the "data" property
                var dataElement = jsonDoc.RootElement.GetPropertyOrNull("data") ?? jsonDoc.RootElement;

                // Unified parsing of the switcher menu structure
                var menu = dataElement.GetPropertyOrNull("actions")
                    ?.EnumerateArrayOrNull()?.FirstOrDefault()
                    .GetPropertyOrNull("getMultiPageMenuAction")?.GetPropertyOrNull("menu")?.GetPropertyOrNull("multiPageMenuRenderer")
                    ?? dataElement.GetPropertyOrNull("actions")
                    ?.EnumerateArrayOrNull()?.FirstOrDefault()
                    .GetPropertyOrNull("openPopupAction")?.GetPropertyOrNull("popup")?.GetPropertyOrNull("multiPageMenuRenderer");

                // Local function for parsing an individual account wrapper to avoid duplication
                void ProcessWrap(JsonElement wrap, string sectionEmail)
                {
                    var accountItem = wrap.GetPropertyOrNull("accountItem");
                    if (accountItem == null) return;

                    var nameEl = accountItem.Value.GetPropertyOrNull("accountName");
                    var name = nameEl.HasValue ? GetStringValue(nameEl.Value) ?? "Unknown" : "Unknown";

                    var avatar = accountItem.Value.GetPropertyOrNull("accountPhoto")?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull()?.LastOrDefault().GetPropertyOrNull("url")?.GetStringOrNull() ?? "";
                    bool isSelected = accountItem.Value.GetPropertyOrNull("isSelected")?.GetBoolean() ?? false;

                    string gaiaId = "";

                    var handleEl = accountItem.Value.GetPropertyOrNull("channelHandle");
                    string handle = handleEl.HasValue ? GetStringValue(handleEl.Value) ?? "" : "";

                    string parsedAuthUser = AuthState.DefaultAuthUser;

                    var tokens = accountItem.Value.GetPropertyOrNull("serviceEndpoint")?.GetPropertyOrNull("selectActiveIdentityEndpoint")?.GetPropertyOrNull("supportedTokens")?.EnumerateArrayOrNull();
                    if (tokens != null)
                    {
                        foreach (var token in tokens.Value)
                        {
                            var stateToken = token.GetPropertyOrNull("accountStateToken");
                            if (stateToken != null) gaiaId = stateToken.Value.GetPropertyOrNull("obfuscatedGaiaId")?.GetStringOrNull() ?? gaiaId;

                            var signinToken = token.GetPropertyOrNull("accountSigninToken");
                            if (signinToken != null)
                            {
                                var signinUrl = signinToken.Value.GetPropertyOrNull("signinUrl")?.GetStringOrNull();
                                if (!string.IsNullOrEmpty(signinUrl))
                                {
                                    int targetIdx = signinUrl.IndexOf("authuser=");
                                    if (targetIdx >= 0)
                                    {
                                        var remaining = signinUrl.Substring(targetIdx + 9);
                                        int ampIdx = remaining.IndexOf('&');
                                        parsedAuthUser = ampIdx >= 0 ? remaining.Substring(0, ampIdx) : remaining;
                                    }
                                }
                            }
                        }
                    }

                    results.Add(new YoutubeAccountItem
                    {
                        Index = results.Count + 1,
                        Name = name,
                        Email = sectionEmail,
                        AvatarUrl = avatar,
                        GaiaId = gaiaId,
                        Handle = handle,
                        AuthUser = parsedAuthUser,
                        IsSelected = isSelected
                    });

                    if (isSelected)
                    {
                        hasActiveAccount = true;
                        activeName = name;
                        activeEmail = sectionEmail;
                        activeAvatar = avatar;
                        activeHandle = handle;
                        activeGaiaId = gaiaId;
                    }
                }

                var sections = menu?.GetPropertyOrNull("sections")?.EnumerateArrayOrNull();
                if (sections != null)
                {
                    foreach (var section in sections.Value)
                    {
                        var accountSection = section.GetPropertyOrNull("accountSectionListRenderer");
                        if (accountSection == null) continue;

                        string sectionEmail = "";

                        var header = accountSection.Value.GetPropertyOrNull("header");
                        if (header != null)
                        {
                            var googleHeader = header.Value.GetPropertyOrNull("googleAccountHeaderRenderer");
                            if (googleHeader != null)
                            {
                                var emailEl = googleHeader.Value.GetPropertyOrNull("email");
                                sectionEmail = emailEl.HasValue ? GetStringValue(emailEl.Value) ?? "" : "";
                            }
                            else
                            {
                                var itemHeader = header.Value.GetPropertyOrNull("accountItemSectionHeaderRenderer");
                                if (itemHeader != null)
                                {
                                    var titleEl = itemHeader.Value.GetPropertyOrNull("title");
                                    sectionEmail = titleEl.HasValue ? GetStringValue(titleEl.Value) ?? "" : "";
                                }
                            }
                        }

                        var contents = accountSection.Value.GetPropertyOrNull("contents")?.EnumerateArrayOrNull();
                        if (contents == null) continue;

                        foreach (var itemWrap in contents.Value)
                        {
                            var accItemSection = itemWrap.GetPropertyOrNull("accountItemSectionRenderer");
                            var subItems = accItemSection?.GetPropertyOrNull("contents")?.EnumerateArrayOrNull();

                            if (subItems != null)
                            {
                                foreach (var subItemWrap in subItems.Value)
                                {
                                    ProcessWrap(subItemWrap, sectionEmail);
                                }
                            }
                            else
                            {
                                ProcessWrap(itemWrap, sectionEmail);
                            }
                        }
                    }
                }

                if (hasActiveAccount)
                {
                    UpdateCachedAccounts(results);
                    UpdateUserProfile(activeName ?? "User", activeEmail ?? "", activeAvatar ?? "", activeGaiaId ?? "");
                    return (true, null, false);
                }

                // Google successfully returned 200 OK, but it contains no active account (guest menu).
                // This means the session was invalidated by the server. Force logout!
                Log.Warn("[Auth] Validation returned guest configuration. Session expired. Automatically logging out...");
                Logout();
                return (false, "Session expired or returned guest menu configuration.", false);
            }

            return (false, $"Validation returned status {response.StatusCode}", true);
        }
        catch (OperationCanceledException)
        {
            // External cancellation — rethrow so the caller can distinguish
            // user cancel from internal timeout
            if (ct.IsCancellationRequested)
                throw;

            // Internal CancelAfter(8s) triggered — this is a network timeout, not a user cancel
            Log.Warn("[Auth] Session validation timed out (8s internal deadline)");
            return (false, "Connection timed out", true);
        }
        catch (Exception ex)
        {
            // Important: On network timeouts we do NOT call Logout(),
            // as cookies may still be valid, connection is just temporarily lost.
            Log.Error($"[Auth] Session validation exception: {ex.Message}");
            return (false, ex.Message, true);
        }
    }

    /// <summary>
    /// Sets the active Google multi-session index.
    /// The change event is published only when the value actually changes.
    /// </summary>
    /// <param name="authUser">Active authuser index.</param>
    public void SetAuthUser(string authUser)
    {
        bool changed;
        lock (_lock)
        {
            changed = !string.Equals(State.AuthUser, authUser, StringComparison.Ordinal);
            if (!changed)
                return;

            State.AuthUser = authUser;
        }

        SaveAuthData();
        OnAuthStateChanged?.Invoke();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetCookieHeader()
    {
        lock (_lock) return _cachedHeaderString;
    }

    public string? GetCookieValue(string key)
    {
        lock (_lock)
            return _cookieMap.TryGetValue(key, out var val) ? val : null;
    }

    /// <summary>
    /// Saves cookies received from the browser extension and updates the authentication state.
    /// <para>
    /// On fresh login (unauth → auth), <see cref="OnAuthStateChanged"/> <b>does not fire</b>,
    /// since <see cref="AuthState.ActiveGaiaId"/> is not set yet and the library's <c>CurrentOwnerId</c>
    /// will return <c>"guest"</c>. Instead, it launches background session validation,
    /// upon completion of which <see cref="UpdateUserProfile"/> sets the correct profile
    /// and fires <see cref="OnAuthStateChanged"/> with the correct owner context.
    /// </para>
    /// </summary>
    public void SaveCookies(string cookies)
    {
        if (string.IsNullOrWhiteSpace(cookies)) return;

        var span = cookies.AsSpan().Trim().Trim('"');
        var clean = span.ToString().Replace("\r", "").Replace("\n", "");
        clean = FindCookieTextRegex().Replace(clean, "");

        if (!clean.Contains("SAPISID", StringComparison.Ordinal))
        {
            Log.Warn("[Auth] Attempt to save cookies without SAPISID. Ignoring.");
            return;
        }

        bool wasAuthenticated;
        lock (_lock)
        {
            wasAuthenticated = _cookieMap.ContainsKey("SAPISID");
        }

        ParseAndSetCookies(clean);
        SaveCookiesToFile();
        UpdateStateAuthStatus();

        bool isFreshLogin = !wasAuthenticated && IsAuthenticated;

        if (isFreshLogin)
        {
            // Fresh login: ActiveGaiaId is not set yet → CurrentOwnerId = "guest".
            // Do NOT fire OnAuthStateChanged — otherwise LibraryService hydrates for "guest".
            // Launch background validation → UpdateUserProfile → OnAuthStateChanged with correct GaiaId.
            Log.Info("[Auth] Fresh authentication detected. Deferring auth state notification until profile is validated.");
            _ = ValidateAndNotifyAsync();
        }
        else
        {
            // Cookie update for an existing session — profile is already set
            OnAuthStateChanged?.Invoke();
        }

        Log.Info($"[Auth] Cookies saved. Fresh login: {isFreshLogin}. Total keys: {_cookieMap.Count}");
    }

    /// <summary>
    /// Performs deferred session validation after a fresh login.
    /// Previous pending call is canceled via <see cref="_validateCts"/>.
    /// On network error, displays an informative notification to the user.
    /// </summary>
    private async Task ValidateAndNotifyAsync()
    {
        CancellationTokenSource cts;
        lock (_validateLock)
        {
            _validateCts?.Cancel();
            _validateCts?.Dispose();
            cts = _validateCts = new CancellationTokenSource();
        }

        try
        {
            var (isValid, error, isNetworkError) = await ValidateSessionAsync(cts.Token).ConfigureAwait(false);

            if (cts.IsCancellationRequested) return;

            if (isNetworkError)
            {
                Log.Warn($"[Auth] Post-login validation failed due to network: {error}. Firing fallback auth state change.");
                _profileLoadError = error;
                OnAuthStateChanged?.Invoke();
            }
            // Success → UpdateUserProfile already raised OnAuthStateChanged
            // 401     → Logout() already raised OnAuthStateChanged
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                Log.Warn($"[Auth] Deferred session validation failed: {ex.Message}. Firing fallback auth state change.");
                _profileLoadError = ex.Message;
                OnAuthStateChanged?.Invoke();
            }
        }
        finally
        {
            lock (_validateLock)
            {
                if (_validateCts == cts)
                {
                    cts.Dispose();
                    _validateCts = null;
                }
            }
        }
    }

    public bool UpdateCookies(IEnumerable<string> setCookieHeaders)
    {
        bool criticalCookieUpdated = false;
        lock (_lock)
        {
            foreach (var header in setCookieHeaders)
            {
                var headerSpan = header.AsSpan();
                int semicolonIdx = headerSpan.IndexOf(';');
                var firstPart = semicolonIdx >= 0 ? headerSpan[..semicolonIdx] : headerSpan;

                int equalIdx = firstPart.IndexOf('=');
                if (equalIdx <= 0) continue;

                var key = firstPart[..equalIdx].Trim().ToString();
                var value = firstPart[(equalIdx + 1)..].Trim().ToString();

                if (string.IsNullOrEmpty(value) ||
                    value.Equals("deleted", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("expired", StringComparison.OrdinalIgnoreCase))
                {
                    if (_cookieMap.Remove(key))
                    {
                        Log.Debug($"[Auth] Cookie '{key}' deleted by server request.");
                    }
                    continue;
                }

                if (!_cookieMap.TryGetValue(key, out var existingVal) || existingVal != value)
                {
                    _cookieMap[key] = value;
                    criticalCookieUpdated = true;
                }
            }

            if (criticalCookieUpdated || _cookieMap.Count > 0)
                RebuildHeaderString();
        }

        if (criticalCookieUpdated)
        {
            SaveCookiesToFile();
            UpdateStateAuthStatus();
        }

        return criticalCookieUpdated;
    }

    public void Logout()
    {
        // Cancel before OnAuthStateChanged — guarantee validate won't fire after logout
        lock (_validateLock)
        {
            _validateCts?.Cancel();
            _validateCts?.Dispose();
            _validateCts = null;
        }

        lock (_lock)
        {
            _cookieMap.Clear();
            _cachedHeaderString = "";
        }

        if (File.Exists(G.FilePath.Cookie)) File.Delete(G.FilePath.Cookie);
        var cookieBak = string.Concat(G.FilePath.Cookie, ".bak");
        if (File.Exists(cookieBak)) File.Delete(cookieBak);

        State = new AuthState();
        if (File.Exists(_authDataPath)) File.Delete(_authDataPath);
        var authBak = string.Concat(_authDataPath, ".bak");
        if (File.Exists(authBak)) File.Delete(authBak);

        OnAuthStateChanged?.Invoke();
    }

    private void ParseAndSetCookies(string raw)
    {
        lock (_lock)
        {
            _cookieMap.Clear();
            var remaining = raw.AsSpan();

            while (remaining.Length > 0)
            {
                int semicolonIdx = remaining.IndexOf(';');
                ReadOnlySpan<char> part;

                if (semicolonIdx >= 0)
                {
                    part = remaining[..semicolonIdx];
                    remaining = remaining[(semicolonIdx + 1)..];
                }
                else
                {
                    part = remaining;
                    remaining = [];
                }

                int equalIdx = part.IndexOf('=');
                if (equalIdx <= 0) continue;

                var key = part[..equalIdx].Trim();
                var value = part[(equalIdx + 1)..].Trim();

                if (key.Length > 0)
                    _cookieMap[key.ToString()] = value.ToString();
            }

            RebuildHeaderString();
        }
    }

    private void RebuildHeaderString()
    {
        if (_cookieMap.Count == 0)
        {
            _cachedHeaderString = "";
            return;
        }

        int estimatedLen = _cookieMap.Count * 40;
        var sb = new StringBuilder(estimatedLen);

        foreach (var kvp in _cookieMap)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(kvp.Key);
            sb.Append('=');
            sb.Append(kvp.Value);
        }

        _cachedHeaderString = sb.ToString();
    }

    // --- Non-blocking Save ---

    /// <summary>
    /// Serializes the profile synchronously (CPU-bound, ~0.1 ms);
    /// disk write is fire-and-forget via <see cref="_authSaveSemaphore"/>.
    /// </summary>
    public void SaveAuthData()
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(State, AppJsonContext.DefaultPretty.AuthState);
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Failed to serialize auth data: {ex.Message}");
            return;
        }
        _ = WriteAuthDataAsync(json);
    }

    private async Task WriteAuthDataAsync(string json)
    {
        await _authSaveSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await AtomicFile.WriteTextAsync(_authDataPath, json, createBackup: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Failed to save auth data: {ex.Message}");
        }
        finally
        {
            _authSaveSemaphore.Release();
        }
    }

    private void SaveCookiesToFile()
    {
        string? content;
        lock (_lock)
        {
            if (!_cookieMap.ContainsKey("SAPISID")) return;
            content = _cachedHeaderString;
        }
        _ = WriteCookiesToFileAsync(content);
    }

    private async Task WriteCookiesToFileAsync(string content)
    {
        await _cookieSaveSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await AtomicFile.WriteTextAsync(G.FilePath.Cookie, content, createBackup: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Failed to save updated cookies: {ex.Message}");
        }
        finally
        {
            _cookieSaveSemaphore.Release();
        }
    }

    [GeneratedRegex(@"^Cookie:\s*", RegexOptions.IgnoreCase, "ru-RU")]
    private static partial Regex FindCookieTextRegex();
}