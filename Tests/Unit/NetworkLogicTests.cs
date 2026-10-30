using System.Net;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные тесты сетевой логики: разбор параметров TUN/VPN маршрутизации,
/// валидация адаптеров и подавление First-Chance исключений при отмене задержек.
/// </summary>
public static class NetworkLogicTests
{
    [TestMethod(TestCategory.Unit, "NetworkManager Route Evaluation & Tun Parsing (0 Alloc)", Group = TestGroups.Network, Order = 1)]
    public static Task TestNetworkManagerLogicZeroAllocAsync()
    {
        var isSpecialTunMethod = typeof(NetworkManager).GetMethod(
            "IsSpecialTunIpRange",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("IsSpecialTunIpRange not found");

        var containsVpnMethod = typeof(NetworkManager).GetMethod(
            "ContainsVpnKeyword",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("ContainsVpnKeyword not found");

        var isSpecialTunDel = (Func<IPAddress, bool>)Delegate.CreateDelegate(
            typeof(Func<IPAddress, bool>), isSpecialTunMethod);
        var containsVpnDel = (Func<string?, bool>)Delegate.CreateDelegate(
            typeof(Func<string?, bool>), containsVpnMethod);

        var tunIp = IPAddress.Parse("198.18.0.1");
        var lanIp = IPAddress.Parse("192.168.1.100");

        TestAssert.True(isSpecialTunDel(tunIp), "198.18.0.1 must be recognized as TUN IP");
        TestAssert.False(isSpecialTunDel(lanIp), "192.168.1.100 must not be recognized as TUN IP");

        TestAssert.True(containsVpnDel("Mihomo Virtual TUN Adapter"), "Failed to match VPN adapter");
        TestAssert.False(containsVpnDel("Realtek PCIe GbE Family Controller"), "False-positive VPN match");

        // Проверка отсутствия аллокаций
        TestAssert.ZeroAlloc(() =>
        {
            _ = isSpecialTunDel(tunIp);
            _ = isSpecialTunDel(lanIp);
            _ = containsVpnDel("sing-box Virtual TUN");
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
        // 1. Внутренний таймаут HttpClient при не отменённом внешнем токене -> Timeout
        var internalTimeoutEx = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        var classifiedTimeout = Core.Youtube.Exceptions.YoutubeNetworkException.TryClassify(internalTimeoutEx, CancellationToken.None);
        TestAssert.NotNull(classifiedTimeout, "HttpClient internal timeout must be classified as YoutubeNetworkException");
        TestAssert.True(classifiedTimeout.IsTimeout, "Expected IsTimeout == true");
        TestAssert.Equal("Error_Network_Timeout", classifiedTimeout.GetLocalizationKey());
        TestAssert.Equal("Recommendation_CheckNetwork", classifiedTimeout.GetRecommendationKey());

        // 2. Отмена самим пользователем (внешний токен отменён) -> обязана вернуть null, а не ложную ошибку сети!
        using var userCts = new CancellationTokenSource();
        userCts.Cancel();
        var userCancelEx = new TaskCanceledException("User switched track", null, userCts.Token);
        var classifiedUserCancel = Core.Youtube.Exceptions.YoutubeNetworkException.TryClassify(userCancelEx, userCts.Token);
        TestAssert.Null(classifiedUserCancel, "User-initiated cancellation must NEVER be classified as a network error");

        // 3. Ошибка SSL/TLS Handshake (DPI / ТСПУ блокировка) -> SslHandshakeFailure
        var sslEx = new HttpRequestException(
            "The SSL connection could not be established",
            new System.Security.Authentication.AuthenticationException("Received an unexpected EOF or 0 bytes from the transport stream."));
        var classifiedSsl = Core.Youtube.Exceptions.YoutubeNetworkException.TryClassify(sslEx, CancellationToken.None);
        TestAssert.NotNull(classifiedSsl);
        TestAssert.True(classifiedSsl.IsSslFailure, "Expected IsSslFailure == true");
        TestAssert.Equal("Error_SslHandshake_Failed", classifiedSsl.GetLocalizationKey());
        TestAssert.Equal("Recommendation_DpiBlocked", classifiedSsl.GetRecommendationKey());

        // 4. Сброс сокета / недоступность сети -> ConnectionFailed
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