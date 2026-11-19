using System.Text.Json.Serialization;

namespace LMP.Core.Models;

/// <summary>
/// Handshake payload sent with IPC opcode 0 (<c>HANDSHAKE</c>).
/// </summary>
public sealed class DiscordHandshakePayload
{
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;
}

/// <summary>
/// Outgoing Discord RPC command envelope sent with IPC opcode 1 (<c>FRAME</c>).
/// </summary>
public sealed class DiscordRpcCommandFrame
{
    [JsonPropertyName("cmd")]
    public string Cmd { get; set; } = "SET_ACTIVITY";

    [JsonPropertyName("args")]
    public DiscordSetActivityArgs Args { get; set; } = new();

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;
}

/// <summary>
/// Arguments for the <c>SET_ACTIVITY</c> RPC command.
/// <see cref="Activity"/> is always serialized (even when <see langword="null"/>) so clearing presence works reliably.
/// </summary>
public sealed class DiscordSetActivityArgs
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("activity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DiscordActivity? Activity { get; set; }
}

/// <summary>
/// Discord Rich Presence activity payload supporting ActivityType, StatusDisplayType, clickable URLs, assets, timestamps, and buttons.
/// </summary>
public sealed class DiscordActivity
{
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("status_display_type")]
    public int? StatusDisplayType { get; set; }

    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("details_url")]
    public string? DetailsUrl { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("state_url")]
    public string? StateUrl { get; set; }

    [JsonPropertyName("timestamps")]
    public DiscordActivityTimestamps? Timestamps { get; set; }

    [JsonPropertyName("assets")]
    public DiscordActivityAssets? Assets { get; set; }

    [JsonPropertyName("buttons")]
    public List<DiscordActivityButton>? Buttons { get; set; }

    [JsonPropertyName("instance")]
    public bool Instance { get; set; }
}

/// <summary>
/// Unix timestamp bounds (in seconds) for activity elapsed/progress display.
/// </summary>
public sealed class DiscordActivityTimestamps
{
    [JsonPropertyName("start")]
    public long? Start { get; set; }

    [JsonPropertyName("end")]
    public long? End { get; set; }
}

/// <summary>
/// Artwork URLs / asset keys and hover tooltips for Discord Rich Presence.
/// </summary>
public sealed class DiscordActivityAssets
{
    [JsonPropertyName("large_image")]
    public string? LargeImage { get; set; }

    [JsonPropertyName("large_text")]
    public string? LargeText { get; set; }

    [JsonPropertyName("large_url")]
    public string? LargeUrl { get; set; }

    [JsonPropertyName("small_image")]
    public string? SmallImage { get; set; }

    [JsonPropertyName("small_text")]
    public string? SmallText { get; set; }

    [JsonPropertyName("small_url")]
    public string? SmallUrl { get; set; }
}

/// <summary>
/// External link button rendered on another user's view of the Rich Presence card (max 2 per activity).
/// </summary>
public sealed class DiscordActivityButton
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}