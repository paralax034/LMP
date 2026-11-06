using System.Net;
using System.Runtime.CompilerServices;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit tests for network logic: parsing TUN/VPN routing parameters,
/// adapter validation, and suppressing First-Chance exceptions during delay cancellation.
/// </summary>
public static class NetworkLogicTests
{
    // Static access to private methods without reflection: binding occurs at JIT/AOT stage, trimmer sees both references
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "IsSpecialTunIpRange")]
    private static extern bool IsSpecialTunIpRange(NetworkManager? owner, IPAddress ip);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ContainsVpnKeyword")]
    private static extern bool ContainsVpnKeyword(NetworkManager? owner, string? text);

    [TestMethod(TestCategory.Unit, "NetworkManager Route Evaluation & Tun Parsing (0 Alloc)", Group = TestGroups.Network, Order = 1)]
    public static Task TestNetworkManagerLogicZeroAllocAsync()
    {
        var tunIp = IPAddress.Parse("198.18.0.1");
        var lanIp = IPAddress.Parse("192.168.1.100");

        TestAssert.True(IsSpecialTunIpRange(null, tunIp), "198.18.0.1 must be recognized as TUN IP");
        TestAssert.False(IsSpecialTunIpRange(null, lanIp), "192.168.1.100 must not be recognized as TUN IP");

        TestAssert.True(ContainsVpnKeyword(null, "Mihomo Virtual TUN Adapter"), "Failed to match VPN adapter");
        TestAssert.False(ContainsVpnKeyword(null, "Realtek PCIe GbE Family Controller"), "False-positive VPN match");

        // Word boundaries in IsTunOrTapIdentifier: standalone tun/tap and utunN are tunnels, "Tun" inside a word is not
        TestAssert.True(ContainsVpnKeyword(null, "utun3"), "macOS-style utunN must be recognized as TUN");
        TestAssert.True(ContainsVpnKeyword(null, "TAP-Windows Adapter V9"), "Standalone TAP token must be recognized");
        TestAssert.False(ContainsVpnKeyword(null, "Hauppauge TV Tuner"), "'Tun' inside a word must not match");
        TestAssert.False(ContainsVpnKeyword(null, null), "Null description must not match");

        // Allocation freedom check
        TestAssert.ZeroAlloc(() =>
        {
            _ = IsSpecialTunIpRange(null, tunIp);
            _ = IsSpecialTunIpRange(null, lanIp);
            _ = ContainsVpnKeyword(null, "sing-box Virtual TUN");
        }, warmup: 5, iterations: 1000);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "DelayNoThrow Cancellation Zero First-Chance Exceptions", Group = TestGroups.Network, Order = 2)]
    public static async Task TestDelayNoThrowCancellationAsync()
    {
        int cancelExceptionsCount = 0;
        void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is OperationCanceledException)
                Interlocked.Increment(ref cancelExceptionsCount);
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        try
        {
            using var cts = new CancellationTokenSource(millisecondsDelay: 15);
            bool completedNormally = await cts.Token.DelayNoThrowAsync(5_000, continueOnCapturedContext: false).ConfigureAwait(false);

            TestAssert.False(completedNormally, "DelayNoThrowAsync must return false on cancel");
            TestAssert.Equal(0, cancelExceptionsCount, "SuppressThrowing must not raise first-chance exceptions");
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
        }
    }

    [TestMethod(TestCategory.Unit, "YoutubeNetworkException Classification & User-Cancel Guard", Group = TestGroups.Network, Order = 3)]
    public static Task TestYoutubeNetworkExceptionClassificationAsync()
    {
        // 1. Internal HttpClient timeout when the external token is not canceled -> Timeout
        var internalTimeoutEx = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        var classifiedTimeout = Core.Youtube.Exceptions.YoutubeNetworkException.TryClassify(internalTimeoutEx, CancellationToken.None);
        TestAssert.NotNull(classifiedTimeout, "HttpClient internal timeout must be classified as YoutubeNetworkException");
        TestAssert.True(classifiedTimeout.IsTimeout, "Expected IsTimeout == true");
        TestAssert.Equal("Error_Network_Timeout", classifiedTimeout.GetLocalizationKey());
        TestAssert.Equal("Recommendation_CheckNetwork", classifiedTimeout.GetRecommendationKey());

        // 2. Cancellation by the user (external token canceled) -> must return null, not a false network error!
        using var userCts = new CancellationTokenSource();
        userCts.Cancel();
        var userCancelEx = new TaskCanceledException("User switched track", null, userCts.Token);
        var classifiedUserCancel = Core.Youtube.Exceptions.YoutubeNetworkException.TryClassify(userCancelEx, userCts.Token);
        TestAssert.Null(classifiedUserCancel, "User-initiated cancellation must NEVER be classified as a network error");

        // 3. SSL/TLS Handshake error (DPI / TSPU blocking) -> SslHandshakeFailure
        var sslEx = new HttpRequestException(
            "The SSL connection could not be established",
            new System.Security.Authentication.AuthenticationException("Received an unexpected EOF or 0 bytes from the transport stream."));
        var classifiedSsl = Core.Youtube.Exceptions.YoutubeNetworkException.TryClassify(sslEx, CancellationToken.None);
        TestAssert.NotNull(classifiedSsl);
        TestAssert.True(classifiedSsl.IsSslFailure, "Expected IsSslFailure == true");
        TestAssert.Equal("Error_SslHandshake_Failed", classifiedSsl.GetLocalizationKey());
        TestAssert.Equal("Recommendation_DpiBlocked", classifiedSsl.GetRecommendationKey());

        // 4. Socket reset / network unreachable -> ConnectionFailed
        var socketEx = new HttpRequestException(
            "Connection reset by peer",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset));
        var classifiedConn = Core.Youtube.Exceptions.YoutubeNetworkException.TryClassify(socketEx, CancellationToken.None);
        TestAssert.NotNull(classifiedConn);
        TestAssert.True(classifiedConn.IsConnectionFailed, "Expected IsConnectionFailed == true");
        TestAssert.Equal("Error_Network_ConnectionFailed", classifiedConn.GetLocalizationKey());

        return Task.CompletedTask;
    }
}