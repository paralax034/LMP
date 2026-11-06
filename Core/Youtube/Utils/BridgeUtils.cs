using System.Runtime.CompilerServices;
using System.Text.Json;

namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Utility class for general YouTube API response parsing operations.
/// Contains optimized methods for extracting continuation tokens.
/// </summary>
internal static class BridgeUtils
{
    /// <summary>
    /// Tries to find a continuation token in various JSON response structures.
    /// Supports formats:
    /// 1. commandExecutorCommand (nested commands, used in "Liked").
    /// 2. continuationCommand (direct token).
    /// 3. nextContinuationData (legacy format).
    /// </summary>
    /// <param name="renderer">JSON element representing continuationItemRenderer or similar object.</param>
    /// <returns>Token string or null if token is not found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? ExtractContinuationToken(JsonElement renderer)
    {
        if (renderer.TryGetProperty("continuationEndpoint", out var endpoint))
        {
            if (endpoint.TryGetProperty("continuationCommand", out var directCommand) &&
                directCommand.TryGetProperty("token", out var directToken))
            {
                return directToken.GetStringOrNull();
            }

            if (endpoint.TryGetProperty("commandExecutorCommand", out var executor) &&
                executor.TryGetProperty("commands", out var commands) &&
                commands.ValueKind == JsonValueKind.Array)
            {
                foreach (var cmd in commands.EnumerateArray())
                {
                    if (cmd.TryGetProperty("continuationCommand", out var nestedCommand) &&
                        nestedCommand.TryGetProperty("token", out var nestedToken))
                    {
                        return nestedToken.GetStringOrNull();
                    }
                }
            }
        }

        if (renderer.TryGetProperty("nextContinuationData", out var nextData) &&
            nextData.TryGetProperty("continuation", out var oldToken))
        {
            return oldToken.GetStringOrNull();
        }

        return null;
    }

    /// <summary>
    /// Searches for a continuationItemRenderer element at the end of the contents list.
    /// </summary>
    /// <param name="contents">JSON array of contents (contents/items).</param>
    /// <returns>Continuation token if the last renderer element is found.</returns>
    public static string? FindTokenInContents(JsonElement contents)
    {
        if (contents.ValueKind != JsonValueKind.Array)
            return null;

        int len = contents.GetArrayLength();
        if (len == 0)
            return null;

        var lastItem = contents[len - 1];

        if (lastItem.ValueKind == JsonValueKind.Undefined)
            return null;

        if (lastItem.TryGetProperty("continuationItemRenderer", out var renderer))
        {
            return ExtractContinuationToken(renderer);
        }

        return null;
    }
}