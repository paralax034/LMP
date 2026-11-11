using LMP.Core.Audio.Http;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Centralized string identifiers for InnerTube API clients.
/// </summary>
internal static class YoutubeClientNames
{
	public const string AndroidVr = "ANDROID_VR";
	public const string AndroidMusic = "ANDROID_MUSIC";
	public const string WebRemix = "WEB_REMIX";
	public const string Web = "WEB";
	public const string Tv = "TVHTML5_SIMPLY_EMBEDDED_PLAYER";
	public const string Ios = "IOS";
	public const string AndroidTestSuite = "ANDROID_TESTSUITE";
}

internal static class YoutubeClientUtils
{
	public static YoutubeClientProfile CurrentProfile { get; set; } = YoutubeClientProfile.AndroidVR;

	private const string DefaultVisitorData = "CgtsZG1ySnZiQUkSbyiMjuGSBg%3D%3D";

	/// <summary>
	/// Suppression time for repeated requests to <c>sw.js_data</c> after a network failure (ms),
	/// during which the fallback <see cref="DefaultVisitorData"/> is used.
	/// </summary>
	private const long VisitorDataFailureCooldownMs = 60_000;

	/// <summary>
	/// Maximum time to wait for a response from <c>sw.js_data</c> when resolving VisitorData.
	/// </summary>
	private static readonly TimeSpan SwDataFetchTimeout = TimeSpan.FromSeconds(5);

	private static string _visitorData = DefaultVisitorData;
	private static long _lastFailedFetchTick;
	private static Task<string>? _fetchTask;
	private static CookieAuthService? _authService;
	private static Func<HttpClient>? _httpClientProvider;
	private static Func<bool>? _vpnActiveCheck;
	private static readonly Lock _visitorDataLock = new();

	private const string InvariantHlJson = "\"en\"";
	private const string InvariantGlJson = "\"US\"";

	/// <summary>
	/// Indicates whether a VPN/TUN route is active according to the network manager.
	/// </summary>
	public static bool IsVpnActive => _vpnActiveCheck?.Invoke() ?? false;

	/// <summary>
	/// Initializes the static provider of auth cookies, HTTP client, and VPN detector for VisitorData and client selection.
	/// </summary>
	public static void Initialize(
		CookieAuthService authService,
		Func<HttpClient>? httpClientProvider = null,
		Func<bool>? vpnActiveCheck = null)
	{
		_authService = authService;
		_httpClientProvider = httpClientProvider;
		_vpnActiveCheck = vpnActiveCheck;
	}

	/// <summary>
	/// Unified, globally synchronized VisitorData token for all YouTube clients.
	/// Prevents duplicate network requests to sw.js_data on hot playback paths.
	/// </summary>
	public static string VisitorData
	{
		get { lock (_visitorDataLock) return _visitorData; }
		set
		{
			if (string.IsNullOrWhiteSpace(value)) return;
			lock (_visitorDataLock)
			{
				_visitorData = value;
				_lastFailedFetchTick = 0;
			}
		}
	}

	/// <summary>
	/// Ensures the presence of fresh VisitorData.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <paramref name="ct"/> is used to cancel **waiting** for the result,
	/// but does not cancel the network request itself — the fetch lives independently of the calling operation.
	/// This prevents a situation where cancelling a single track corrupts the global VisitorData cache.
	/// </para>
	/// </remarks>
	/// <param name="forceRefresh">Forces a refresh even if an up-to-date value exists.</param>
	/// <param name="ct">Cancellation token for waiting.</param>
	public static Task<string> EnsureVisitorDataAsync(bool forceRefresh = false, CancellationToken ct = default)
	{
		lock (_visitorDataLock)
		{
			if (!forceRefresh && !string.Equals(_visitorData, DefaultVisitorData, StringComparison.Ordinal))
				return Task.FromResult(_visitorData);

			// If there was a recent network error fetching sw.js_data, immediately return DefaultVisitorData,
			// without blocking each /player request for an extra 5-10 second timeout.
			long now = Environment.TickCount64;
			if (!forceRefresh && _lastFailedFetchTick != 0 && (now - _lastFailedFetchTick) < VisitorDataFailureCooldownMs)
				return Task.FromResult(_visitorData);

			// Reuse active pending task — deduplication of concurrent calls
			if (!forceRefresh && _fetchTask is { IsCompleted: false } activeTask)
				return ct.CanBeCanceled ? activeTask.WaitAsync(ct) : activeTask;

			// Replace completed task (including faulted/cancelled) with a new one
			_fetchTask = FetchVisitorDataInternalAsync(CancellationToken.None);
			return ct.CanBeCanceled ? _fetchTask.WaitAsync(ct) : _fetchTask;
		}
	}

	/// <summary>
	/// Centralized method for executing network requests to sw.js_data.
	/// Returns VisitorData and new cookie headers for the session.
	/// </summary>
	public static async Task<(string? VisitorData, IEnumerable<string>? SetCookieHeaders, bool IsSuccess)> FetchSwDataAsync(
		string? cookiesHeader, CancellationToken ct)
	{
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/sw.js_data")
			{
				Version = System.Net.HttpVersion.Version11 // Exclude slow HTTP/3 UDP handshakes at startup
			};
			request.Headers.UserAgent.ParseAdd(UaWeb);
			request.Headers.Add("Referer", YoutubeHttpHandler.YoutubeOrigin + "/");

			if (!string.IsNullOrEmpty(cookiesHeader))
			{
				request.Headers.Add("Cookie", cookiesHeader);
			}

			using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(SwDataFetchTimeout);

			var client = _httpClientProvider?.Invoke() ?? SharedHttpClient.Instance;

			using var response = await client.SendAsync(
					request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
			{
				return (null, null, false);
			}

			using var buffer = new InnerTubeStreamBuffer(16 * 1024);
			await using (var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
			{
				await buffer.ReadAllAsync(stream, cts.Token).ConfigureAwait(false);
			}

			var memory = buffer.WrittenMemory;
			if (memory.Span.StartsWith(")]}'"u8))
				memory = memory[4..];

			using var doc = System.Text.Json.JsonDocument.Parse(memory);
			var value = doc.RootElement[0][2][0][0][13].GetStringOrNull();

			response.Headers.TryGetValues("Set-Cookie", out var setCookies);

			return (value, setCookies, true);
		}
		catch (Exception ex)
		{
			Log.Warn($"[YoutubeClientUtils] Failed to fetch sw.js_data: {ex.Message}");
			return (null, null, false);
		}
	}

	private static async Task<string> FetchVisitorDataInternalAsync(CancellationToken ct)
	{
		var cookies = _authService?.GetCookieHeader();
		var (visitorData, setCookies, isSuccess) = await FetchSwDataAsync(cookies, ct).ConfigureAwait(false);

		if (isSuccess && !string.IsNullOrWhiteSpace(visitorData))
		{
			if (setCookies != null && _authService != null)
			{
				_authService.UpdateCookies(setCookies);
			}

			lock (_visitorDataLock)
			{
				_visitorData = visitorData;
				_lastFailedFetchTick = 0;
			}
			return visitorData;
		}

		lock (_visitorDataLock)
		{
			_lastFailedFetchTick = Environment.TickCount64;
			return _visitorData;
		}
	}

	// User-Agents
	public const string UaVr = "com.google.android.apps.youtube.vr.oculus/1.61.48 (Linux; U; Android 12; en_US; Quest 3; Build/SQ3A.220605.009.A1; Cronet/132.0.6808.3)";
	public const string UaTv = "Mozilla/5.0 (PlayStation; PlayStation 4/12.02) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.4 Safari/605.1.15";
	public const string UaWeb = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
	public const string UaWebRemix = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
	public const string UaAndroidMusic = "com.google.android.apps.youtube.music/7.27.52 (Linux; U; Android 14; en_US; Pixel 8 Pro; Build/AP2A.240805.005)";
	public const string UaIos = "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_2 like Mac OS X;)";

	public static string UserAgent => CurrentProfile switch
	{
		YoutubeClientProfile.AndroidVR => UaVr,
		YoutubeClientProfile.TV => UaTv,
		YoutubeClientProfile.Web => UaWeb,
		YoutubeClientProfile.WebRemix => UaWebRemix,
		_ => UaWebRemix
	};

	public static bool RequiresAuth => CurrentProfile is YoutubeClientProfile.Web or YoutubeClientProfile.WebRemix;

	/// <summary>
	/// Default clients for retrieving stream URLs (guest mode).
	/// ANDROID_VR comes first for instant startup (fast path without n-token/cipher),
	/// WEB_REMIX acts as a transparent fallback on bot challenge / age restriction,
	/// WEB acts as a stable web fallback with combined MP4 AAC support without duration limits.
	/// </summary>
	public static readonly string[] StreamFallbackClientsDefault =
	[
		YoutubeClientNames.AndroidVr,
		YoutubeClientNames.WebRemix,
		YoutubeClientNames.Web,
	];

	/// <summary>
	/// Stream fallback for authorized users.
	/// </summary>
	/// <summary>
	/// Stream fallback for authorized users or when BotGuard + VPN is active,
	/// where WEB_REMIX with fast-mint PoToken (2 ms) passes on the first attempt without BotDetection rejection on ANDROID_VR.
	/// </summary>
	public static readonly string[] StreamFallbackClientsWebPriority =
	[
		YoutubeClientNames.WebRemix,
		YoutubeClientNames.AndroidVr,
		YoutubeClientNames.Web,
	];

	public static readonly string[] StreamFallbackClientsAuth =
	[
		YoutubeClientNames.AndroidVr,
		YoutubeClientNames.WebRemix,
		YoutubeClientNames.Web,
	];

	/// <summary>
	/// Returns a list of clients for retrieving stream URLs taking into account BotGuard warmup state and VPN.
	/// </summary>
	public static string[] GetStreamFallbackClients(bool isAuthenticated, bool preferWebRemix = false)
	{
		if (preferWebRemix)
			return StreamFallbackClientsWebPriority;
		return isAuthenticated ? StreamFallbackClientsAuth : StreamFallbackClientsDefault;
	}

	/// <summary>
	/// Backward compatibility — without authorization context.
	/// </summary>
	public static string[] StreamFallbackClients => StreamFallbackClientsDefault;

	/// <summary>
	/// Client ordering after CDN HTTP 403 on previously issued URLs.
	/// WEB comes first (browser profile: it is what plays the track in a browser),
	/// ANDROID_VR comes last: after a series of 403s it almost guaranteed gets bot detection
	/// and goes into a two-hour quarantine.
	/// </summary>
	public static readonly string[] StreamFallbackClientsAfterCdnForbidden =
	[
		YoutubeClientNames.Web,
		YoutubeClientNames.WebRemix,
		YoutubeClientNames.AndroidVr,
	];

	/// <summary>
	/// Returns the fallback chain after CDN 403 excluding clients whose URLs for this track have already been rejected by the CDN.
	/// </summary>
	/// <param name="excludedClients">API names of clients (<c>c</c> parameter value in stream URL) that received CDN 403.</param>
	/// <returns>A new array of clients in order of priority; empty if all are excluded.</returns>
	public static string[] GetStreamFallbackClientsAfterCdnForbidden(IReadOnlyList<string> excludedClients)
	{
		ArgumentNullException.ThrowIfNull(excludedClients);

		var result = new List<string>(StreamFallbackClientsAfterCdnForbidden.Length);
		foreach (var client in StreamFallbackClientsAfterCdnForbidden)
		{
			bool isExcluded = false;
			for (int i = 0; i < excludedClients.Count; i++)
			{
				if (string.Equals(excludedClients[i], client, StringComparison.OrdinalIgnoreCase))
				{
					isExcluded = true;
					break;
				}
			}

			if (!isExcluded)
				result.Add(client);
		}

		return result.ToArray();
	}

	/// <summary>
	/// Returns the string identifier of the client for YouTube API.
	/// Enum.ToString() yields "AndroidVR", whereas the API expects "ANDROID_VR" — direct
	/// conversion via switch prevents errors when adding new clients.
	/// </summary>
	public static string GetClientApiName(YoutubeClientProfile profile) => profile switch
	{
		YoutubeClientProfile.AndroidVR => YoutubeClientNames.AndroidVr,
		YoutubeClientProfile.AndroidMusic => YoutubeClientNames.AndroidMusic,
		YoutubeClientProfile.WebRemix => YoutubeClientNames.WebRemix,
		YoutubeClientProfile.Web => YoutubeClientNames.Web,
		YoutubeClientProfile.TV => YoutubeClientNames.Tv,
		YoutubeClientProfile.Ios => YoutubeClientNames.Ios,
		_ => YoutubeClientNames.WebRemix,
	};

	/// <summary>
	/// Configures specific InnerTube API headers for the outgoing player request.
	/// </summary>
	public static void ConfigurePlayerRequest(HttpRequestMessage request, string clientName)
	{
		request.Headers.Add("User-Agent", GetUserAgentForClient(clientName));

		(string origin, string name, string version)? webProfile = clientName switch
		{
			YoutubeClientNames.Web => (YoutubeHttpHandler.YoutubeOrigin, YoutubeHttpHandler.WebClientName, YoutubeHttpHandler.WebClientVersion),
			YoutubeClientNames.WebRemix => (YoutubeHttpHandler.MusicOrigin, YoutubeHttpHandler.MusicClientName, YoutubeHttpHandler.MusicClientVersion),
			_ => null
		};

		if (webProfile is var (origin, name, version))
		{
			request.Headers.Add("Referer", origin + "/");
			request.Headers.Add("X-YouTube-Client-Name", name);
			request.Headers.Add("X-YouTube-Client-Version", version);
			request.Headers.Add("X-Goog-Api-Format-Version", YoutubeHttpHandler.ApiFormatVersion);
		}
	}

	/// <summary>
	/// Returns the player endpoint URL for the specified client.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static string GetPlayerEndpoint(string clientName)
	{
		var origin = string.Equals(clientName, YoutubeClientNames.WebRemix, StringComparison.Ordinal)
			? YoutubeHttpHandler.MusicOrigin
			: YoutubeHttpHandler.YoutubeOrigin;

		return origin + "/youtubei/v1/player?prettyPrint=false";
	}

	/// <summary>
	/// Generates the player context for the current client profile.
	/// </summary>
	public static string GeneratePlayerContext(string videoId, string? visitorData)
	{
		return GeneratePlayerContextForClient(
			GetClientApiName(CurrentProfile),
			videoId,
			visitorData);
	}

	/// <summary>
	/// Generates context for a specific client with minimal string allocations.
	/// </summary>
	public static string GeneratePlayerContextForClient(string clientName, string videoId, string? visitorData, string? signatureTimestamp = null)
	{
		var vidJson = Json.Serialize(videoId);
		var vdJson = Json.Serialize(visitorData);

		var playbackContextJson = signatureTimestamp != null
			? $@", ""playbackContext"": {{ ""contentPlaybackContext"": {{ ""signatureTimestamp"": {Json.Serialize(signatureTimestamp)} }} }}"
			: "";

		return clientName switch
		{
			YoutubeClientNames.WebRemix => $$"""
			{
				"videoId": {{vidJson}},
				"contentCheckOk": true,
				"racyCheckOk": true,
				"context": {
					"client": {
						"clientName": "WEB_REMIX",
						"clientVersion": "{{YoutubeHttpHandler.MusicClientVersion}}",
						"visitorData": {{vdJson}},
						"hl": {{InvariantHlJson}},
						"gl": {{InvariantGlJson}},
						"utcOffsetMinutes": 0
					}
				}{{playbackContextJson}}
			}
			""",

			YoutubeClientNames.AndroidVr => $$"""
			{
				"videoId": {{vidJson}},
				"contentCheckOk": true,
				"racyCheckOk": true,
				"context": {
					"client": {
						"clientName": "ANDROID_VR",
						"clientVersion": "1.61.48",
						"deviceMake": "Oculus",
						"deviceModel": "Quest 3",
						"osName": "Android",
						"osVersion": "12",
						"androidSdkVersion": "32",
						"platform": "MOBILE",
						"visitorData": {{vdJson}},
						"hl": "en",
						"gl": "US",
						"utcOffsetMinutes": 0
					}
				}
			}
			""",

			YoutubeClientNames.AndroidMusic => $$"""
			{
				"videoId": {{vidJson}},
				"contentCheckOk": true,
				"racyCheckOk": true,
				"context": {
					"client": {
						"clientName": "ANDROID_MUSIC",
						"clientVersion": "7.27.52",
						"androidSdkVersion": "34",
						"osName": "Android",
						"osVersion": "14",
						"platform": "MOBILE",
						"visitorData": {{vdJson}},
						"hl": {{InvariantHlJson}},
						"gl": {{InvariantGlJson}},
						"utcOffsetMinutes": 0
					}
				}
			}
			""",

			YoutubeClientNames.Web => $$"""
			{
				"videoId": {{vidJson}},
				"contentCheckOk": true,
				"racyCheckOk": true,
				"context": {
					"client": {
						"clientName": "WEB",
						"clientVersion": "{{YoutubeHttpHandler.WebClientVersion}}",
						"visitorData": {{vdJson}},
						"hl": {{InvariantHlJson}},
						"gl": {{InvariantGlJson}},
						"utcOffsetMinutes": 0
					}
				}{{playbackContextJson}}
			}
			""",

			YoutubeClientNames.Ios => $$"""
			{
				"videoId": {{vidJson}},
				"contentCheckOk": true,
				"racyCheckOk": true,
				"context": {
					"client": {
						"clientName": "IOS",
						"clientVersion": "20.10.4",
						"deviceMake": "Apple",
						"deviceModel": "iPhone16,2",
						"osName": "iOS",
						"osVersion": "18.2",
						"platform": "MOBILE",
						"visitorData": {{vdJson}},
						"hl": {{InvariantHlJson}},
						"gl": {{InvariantGlJson}},
						"utcOffsetMinutes": 0
					}
				}
			}
			""",

			YoutubeClientNames.Tv => $$"""
			{
				"videoId": {{vidJson}},
				"context": {
					"client": {
						"clientName": "TVHTML5_SIMPLY_EMBEDDED_PLAYER",
						"clientVersion": "2.0",
						"visitorData": {{vdJson}},
						"hl": {{InvariantHlJson}},
						"gl": {{InvariantGlJson}},
						"utcOffsetMinutes": 0,
						"platform": "TV"
					},
					"thirdParty": {
						"embedUrl": "https://www.youtube.com"
					}
				}{{playbackContextJson}}
			}
			""",

			_ => GeneratePlayerContextForClient(YoutubeClientNames.WebRemix, videoId, visitorData, signatureTimestamp)
		};
	}

	/// <summary>
	/// Returns the User-Agent for a specific client.
	/// </summary>
	public static string GetUserAgentForClient(string clientName) => clientName switch
	{
		YoutubeClientNames.WebRemix => UaWebRemix,
		YoutubeClientNames.AndroidVr => UaVr,
		YoutubeClientNames.AndroidMusic => UaAndroidMusic,
		YoutubeClientNames.Web => UaWeb,
		YoutubeClientNames.Ios => UaIos,
		YoutubeClientNames.Tv => UaTv,
		_ => UaWebRemix
	};

	/// <summary>
	/// High-performance duration parser for YouTube media files.
	/// Eliminates string allocations and is optimized for common time formats.
	/// </summary>
	public static class DurationParser
	{
		private static readonly string[] DurationFormats =
			[@"m\:ss", @"mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss"];

		/// <summary>
		/// Performs fast Span-based reading and parsing of the time format.
		/// </summary>
		/// <param name="text">Time string from the InnerTube API.</param>
		/// <returns>Parsed time span, or null.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static TimeSpan? Parse(string? text)
		{
			if (string.IsNullOrWhiteSpace(text)) return null;

			var span = text.AsSpan().Trim();
			int colonCount = 0;
			int firstColon = -1, secondColon = -1;

			for (int i = 0; i < span.Length; i++)
			{
				if (span[i] == ':')
				{
					colonCount++;
					if (colonCount == 1) firstColon = i;
					else if (colonCount == 2) secondColon = i;
				}
			}

			if (colonCount == 1 && firstColon > 0)
			{
				if (int.TryParse(span[..firstColon], out var m) &&
				int.TryParse(span[(firstColon + 1)..], out var s))
				{
					return new TimeSpan(0, m, s);
				}
			}
			else if (colonCount == 2 && firstColon > 0 && secondColon > firstColon)
			{
				if (int.TryParse(span[..firstColon], out var h) &&
				int.TryParse(span[(firstColon + 1)..secondColon], out var m) &&
				int.TryParse(span[(secondColon + 1)..], out var s))
				{
					return new TimeSpan(h, m, s);
				}
			}

			if (TimeSpan.TryParseExact(text, DurationFormats, CultureInfo.InvariantCulture, out var ts))
			{
				return ts;
			}

			return null;
		}
	}
}