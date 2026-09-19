namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Network Events

    private void SubscribeNetworkManagerEvents()
    {
        _networkManager.NetworkRebuilt += HandleNetworkManagerRebuilt;
    }

    private void UnsubscribeNetworkManagerEvents()
    {
        _networkManager.NetworkRebuilt -= HandleNetworkManagerRebuilt;
    }

    private void HandleNetworkManagerRebuilt()
    {
        Log.Info("[AudioEngine] Received NetworkRebuilt from NetworkManager. Pre-warming connections...");
        AudioSourceFactory.PreWarmCdnConnections(
            _networkManager.AudioClient, _lifetimeCts.Token);
    }

    #endregion

    #region Network Starvation Handling

    internal void NotifyNetworkStarvation()
    {
        Log.Info("[AudioEngine] Network starvation detected — forcing HTTP client rebuild via NetworkManager");
        _networkManager.RebuildAll("Audio stream starvation", force: false);
    }

    #endregion

    #region Source-Level Network Events

    private void HandleSourceNetworkStalled(string trackId)
    {
        if (!string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal))
            return;

        Log.Warn($"[AudioEngine] Source-level stall: track='{trackId}' — " +
                 "triggering proactive HTTP rebuild before PCM underrun");

        NotifyNetworkStarvation();
    }

    private void HandleSourceNetworkRecovered(string trackId)
    {
        if (!string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal))
            return;

        Log.Info($"[AudioEngine] Source-level network recovered: track='{trackId}'");

        AudioSourceFactory.PreWarmCdnConnections(
            _networkManager.AudioClient, _lifetimeCts.Token);
    }

    #endregion

    #region Watchdog & Tunnel Helpers

    private void HandleCdnTunnelDead()
    {
        Log.Warn("[AudioEngine] CdnPreWarmer: tunnel dead detected — triggering proactive rebuild");
        NotifyNetworkStarvation();
    }

    #endregion
}