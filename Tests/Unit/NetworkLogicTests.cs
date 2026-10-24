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
}