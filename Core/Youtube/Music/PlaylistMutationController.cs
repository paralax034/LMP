using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Youtube.Music;

internal sealed class PlaylistMutationController(HttpClient http)
{
    private const string ApiUrl = "https://www.youtube.com/youtubei/v1";
    private const string EditPlaylistEndpoint = "browse/edit_playlist";
    private const string CreatePlaylistEndpoint = "playlist/create";
    private const string DeletePlaylistEndpoint = "playlist/delete";

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    /// <summary>
    /// Minimal WEB context — no hl/gl, with useSsl: false.
    /// </summary>
    private static void WriteContext(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(Utils.InnerTubeTokens.Context);
        writer.WriteStartObject();

        writer.WritePropertyName(Utils.InnerTubeTokens.Client);
        writer.WriteStartObject();
        writer.WriteString(Utils.InnerTubeTokens.ClientName, Utils.InnerTubeTokens.Web);
        writer.WriteString(Utils.InnerTubeTokens.ClientVersion, YoutubeHttpHandler.WebClientVersion);
        writer.WriteEndObject(); // client

        writer.WritePropertyName(Utils.InnerTubeTokens.Request);
        writer.WriteStartObject();
        writer.WriteBoolean(Utils.InnerTubeTokens.UseSsl, false);
        writer.WriteEndObject(); // request

        writer.WriteEndObject(); // context
    }

    /// <summary>
    /// Creates HTTP content using ReadOnlyMemoryContent for zero-allocation performance.
    /// </summary>
    private static ReadOnlyMemoryContent CreateJsonContent(Action<Utf8JsonWriter> writeBody)
    {
        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            WriteContext(writer);
            writeBody(writer);
            writer.WriteEndObject();
        }

        var content = new ReadOnlyMemoryContent(bufferWriter.WrittenMemory);
        content.Headers.ContentType = JsonContentType;
        return content;
    }

    /// <summary>
    /// Performs a deterministic POST request to the InnerTube API endpoint.
    /// </summary>
    /// <param name="endpoint">Endpoint name without query parameters.</param>
    /// <param name="writeBody">Delegate for writing the request JSON structure.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed JSON root of the response.</returns>
    private async Task<JsonElement> PostAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/{endpoint}?prettyPrint=false");
        request.Content = CreateJsonContent(writeBody);

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await Json.ParseAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks response for STATUS_FAILED and extracts error message from dialogMessages.
    /// </summary>
    private static void CheckStatus(JsonElement root)
    {
        var status = root.GetPropertyOrNull("status")?.GetStringOrNull();
        if (status == null || status.Equals("STATUS_SUCCEEDED", StringComparison.OrdinalIgnoreCase))
            return;

        if (!status.Equals("STATUS_FAILED", StringComparison.OrdinalIgnoreCase))
            return;

        // Extract error message from confirmDialogEndpoint
        string? message = null;
        try
        {
            var actions = root.GetPropertyOrNull("actions");
            if (actions?.ValueKind == JsonValueKind.Array)
            {
                foreach (var action in actions.Value.EnumerateArray())
                {
                    var dialogMessages = action
                        .GetPropertyOrNull("confirmDialogEndpoint")
                        ?.GetPropertyOrNull("content")
                        ?.GetPropertyOrNull("confirmDialogRenderer")
                        ?.GetPropertyOrNull("dialogMessages");

                    if (dialogMessages?.ValueKind != JsonValueKind.Array) continue;

                    var parts = new List<string>(4);
                    foreach (var msg in dialogMessages.Value.EnumerateArray())
                    {
                        var runs = msg.GetPropertyOrNull("runs");
                        if (runs?.ValueKind != JsonValueKind.Array) continue;

                        foreach (var run in runs.Value.EnumerateArray())
                        {
                            var text = run.GetPropertyOrNull("text")?.GetStringOrNull();
                            if (!string.IsNullOrEmpty(text))
                                parts.Add(text);
                        }
                    }

                    if (parts.Count > 0)
                    {
                        message = string.Join("\n", parts);
                        break;
                    }
                }
            }
        }
        catch
        {
            // Best effort parsing
        }

        throw new YoutubeExplodeException(
            message ?? $"Playlist operation failed with status: {status}");
    }

    /// <summary>
    /// Normalizes a playlist ID by stripping the VL prefix without heap allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string SanitizePlaylistId(string playlistId)
    {
        var span = playlistId.AsSpan();
        if (span.StartsWith("VL", StringComparison.Ordinal) && span.Length > 2)
            return span[2..].ToString();
        return playlistId;
    }

    #region Public API

    /// <summary>
    /// Creates a playlist with optional batch track addition.
    /// </summary>
    /// <param name="title">Playlist title.</param>
    /// <param name="videoIds">Optional list of video IDs to include initially.</param>
    /// <param name="description">Optional playlist description.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>YouTube playlist ID.</returns>
    public async Task<string> CreatePlaylistAsync(
        string title,
        IReadOnlyList<string>? videoIds = null,
        string? description = null,
        CancellationToken ct = default)
    {
        var root = await PostAsync(CreatePlaylistEndpoint, writer =>
        {
            writer.WriteString("title", title);
            if (!string.IsNullOrEmpty(description))
            {
                writer.WriteString("description", description);
            }
            writer.WriteString("params", "ICE%3D");

            if (videoIds is { Count: > 0 })
            {
                writer.WriteStartArray("videoIds");
                for (int i = 0; i < videoIds.Count; i++)
                    writer.WriteStringValue(videoIds[i]);
                writer.WriteEndArray();
            }
        }, ct).ConfigureAwait(false);

        return root.GetPropertyOrNull("playlistId")?.GetStringOrNull()
            ?? throw new YoutubeExplodeException(
                "Failed to create playlist: no playlistId in response.");
    }

    /// <summary>
    /// Batch adds tracks to a playlist in a single request.
    /// </summary>
    /// <returns>setVideoId for each added track (null if individual track failed).</returns>
    public async Task<List<string?>> AddTracksAsync(
        string playlistId,
        IReadOnlyList<string> videoIds,
        CancellationToken ct = default)
    {
        if (videoIds.Count == 0) return [];

        var cleanId = SanitizePlaylistId(playlistId);

        var root = await PostAsync(EditPlaylistEndpoint, writer =>
        {
            writer.WriteString("playlistId", cleanId);

            writer.WriteStartArray("actions");
            for (int i = 0; i < videoIds.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("action", "ACTION_ADD_VIDEO");
                writer.WriteString("addedVideoId", videoIds[i]);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }, ct).ConfigureAwait(false);

        CheckStatus(root);

        // Align response by videoId: the server may skip duplicate or unavailable tracks and shift indices
        var editResults = root.GetPropertyOrNull("playlistEditResults");
        var responseItems = new List<(string? VideoId, string? SetVideoId)>(videoIds.Count);

        if (editResults?.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in editResults.Value.EnumerateArray())
            {
                var added = item.GetPropertyOrNull("playlistEditVideoAddedResultData");
                if (added == null)
                {
                    // Save position: positional fallback is only allowed when response lengths match
                    responseItems.Add((null, null));
                    continue;
                }

                responseItems.Add((
                    added.Value.GetPropertyOrNull("videoId")?.GetStringOrNull(),
                    added.Value.GetPropertyOrNull("setVideoId")?.GetStringOrNull()));
            }
        }

        return PlaylistEditResultAligner.Align(videoIds, responseItems);
    }

    /// <summary>
    /// Batch moves tracks within a playlist on YouTube.
    /// Supports ACTION_MOVE_VIDEO_AFTER and ACTION_MOVE_VIDEO_BEFORE.
    /// </summary>
    public async Task MoveTracksAsync(
        string playlistId,
        IReadOnlyList<(string SetVideoId, string? PredecessorSetVideoId, string? SuccessorSetVideoId)> moves,
        CancellationToken ct = default)
    {
        if (moves.Count == 0) return;

        var cleanPlaylistId = SanitizePlaylistId(playlistId);

        var root = await PostAsync(EditPlaylistEndpoint, writer =>
        {
            writer.WriteString("playlistId", cleanPlaylistId);
            writer.WriteString("params", "CAFAAQ%3D%3D");

            writer.WriteStartArray("actions");
            for (int i = 0; i < moves.Count; i++)
            {
                var (setVideoId, predecessor, successor) = moves[i];
                writer.WriteStartObject();

                if (!string.IsNullOrEmpty(predecessor))
                {
                    writer.WriteString("action", "ACTION_MOVE_VIDEO_AFTER");
                    writer.WriteString("setVideoId", setVideoId);
                    writer.WriteString("movedSetVideoIdPredecessor", predecessor);
                }
                else if (!string.IsNullOrEmpty(successor))
                {
                    writer.WriteString("action", "ACTION_MOVE_VIDEO_BEFORE");
                    writer.WriteString("setVideoId", setVideoId);
                    writer.WriteString("movedSetVideoIdSuccessor", successor);
                }

                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }, ct).ConfigureAwait(false);

        CheckStatus(root);
    }

    /// <summary>
    /// Batch removes tracks from a playlist. Only setVideoId is needed.
    /// </summary>
    public async Task RemoveTracksAsync(
        string playlistId,
        IReadOnlyList<string> setVideoIds,
        CancellationToken ct = default)
    {
        if (setVideoIds.Count == 0) return;

        var cleanPlaylistId = SanitizePlaylistId(playlistId);

        // Filter out empty setVideoId values to avoid 400 Bad Request
        var validSetVideoIds = new List<string>(setVideoIds.Count);
        for (int i = 0; i < setVideoIds.Count; i++)
        {
            var sId = setVideoIds[i];
            if (!string.IsNullOrWhiteSpace(sId) && !sId.Equals("to_be_updated_by_client", StringComparison.OrdinalIgnoreCase))
                validSetVideoIds.Add(sId);
            else
                Log.Warn($"[PlaylistMutation] Skipping track removal at index {i}: empty or invalid setVideoId '{sId}'");
        }

        if (validSetVideoIds.Count == 0)
        {
            Log.Warn("[PlaylistMutation] No valid setVideoIds provided for removal.");
            return;
        }

        var root = await PostAsync(EditPlaylistEndpoint, writer =>
        {
            writer.WriteString("playlistId", cleanPlaylistId);

            writer.WriteStartArray("actions");
            for (int i = 0; i < validSetVideoIds.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("action", "ACTION_REMOVE_VIDEO");
                writer.WriteString("setVideoId", validSetVideoIds[i]);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }, ct).ConfigureAwait(false);

        CheckStatus(root);
    }

    /// <summary>
    /// Renames a playlist.
    /// </summary>
    public Task RenamePlaylistAsync(
        string playlistId,
        string newTitle,
        CancellationToken ct = default) =>
        EditPlaylistDetailsAsync(playlistId, newTitle, description: null, ct);

    /// <summary>
    /// Updates playlist description.
    /// </summary>
    public Task SetPlaylistDescriptionAsync(
        string playlistId,
        string description,
        CancellationToken ct = default) =>
        EditPlaylistDetailsAsync(playlistId, newTitle: null, description, ct);

    /// <summary>
    /// Updates playlist title and/or description in a single edit_playlist request.
    /// </summary>
    public async Task EditPlaylistDetailsAsync(
        string playlistId,
        string? newTitle,
        string? description,
        CancellationToken ct = default)
    {
        if (newTitle == null && description == null) return;

        var root = await PostAsync(EditPlaylistEndpoint, writer =>
        {
            writer.WriteString("playlistId", SanitizePlaylistId(playlistId));

            writer.WriteStartArray("actions");
            if (newTitle != null)
            {
                writer.WriteStartObject();
                writer.WriteString("action", "ACTION_SET_PLAYLIST_NAME");
                writer.WriteString("playlistName", newTitle);
                writer.WriteEndObject();
            }

            if (description != null)
            {
                writer.WriteStartObject();
                writer.WriteString("action", "ACTION_SET_PLAYLIST_DESCRIPTION");
                writer.WriteString("playlistDescription", description);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }, ct).ConfigureAwait(false);

        CheckStatus(root);
    }

    /// <summary>
    /// Deletes a playlist from YouTube account.
    /// </summary>
    public async Task DeletePlaylistAsync(
        string playlistId,
        CancellationToken ct = default)
    {
        var root = await PostAsync(DeletePlaylistEndpoint, writer =>
        {
            writer.WriteString(Utils.InnerTubeTokens.PlaylistId, SanitizePlaylistId(playlistId));
        }, ct).ConfigureAwait(false);

        CheckStatus(root);
    }

    /// <summary>
    /// Uploads a custom thumbnail for a playlist via Scotty Upload Protocol.
    /// 
    /// <para><b>3-step process:</b></para>
    /// <list type="number">
    ///   <item>Initiate: POST with X-Goog-Upload-Command: start → get upload_url</item>
    ///   <item>Upload: POST binary data → get scottyEncryptedBlobId (in JSON response)</item>
    ///   <item>Apply: edit_playlist with ACTION_SET_CUSTOM_THUMBNAIL</item>
    /// </list>
    /// 
    /// <para><b>Important:</b> HttpClient already handles Cookie and Authorization via YoutubeHttpHandler.</para>
    /// </summary>
    /// <param name="playlistId">YouTube playlist ID.</param>
    /// <param name="imageData">Image bytes (JPEG/PNG, recommended 1280x720 or square).</param>
    /// <param name="mimeType">MIME type: "image/jpeg" or "image/png".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if thumbnail was successfully set.</returns>
    public async Task<bool> UploadPlaylistThumbnailAsync(
        string playlistId,
        byte[] imageData,
        CancellationToken ct = default)
    {
        if (imageData.Length == 0)
            throw new ArgumentException("Image data cannot be empty.", nameof(imageData));

        if (imageData.Length > 20 * 1024 * 1024) // 20MB limit
            throw new ArgumentException("Image too large. Maximum size: 20MB.", nameof(imageData));

        try
        {
            // STEP 1: Initiate upload session
            var initiateUrl = "https://music.youtube.com/playlist_image_upload/playlist_custom_thumbnail";

            using var initiateRequest = new HttpRequestMessage(HttpMethod.Post, initiateUrl);

            // Scotty-specific headers
            initiateRequest.Headers.Add("X-Goog-Upload-Protocol", "resumable");
            initiateRequest.Headers.Add("X-Goog-Upload-Command", "start");
            initiateRequest.Headers.Add("X-Goog-Upload-Header-Content-Length", imageData.Length.ToString());

            // Content-Type for empty body
            initiateRequest.Content = new ByteArrayContent([]);
            initiateRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded")
            {
                CharSet = "UTF-8"
            };

            using var initiateResponse = await http.SendAsync(initiateRequest, ct).ConfigureAwait(false);
            initiateResponse.EnsureSuccessStatusCode();

            // Upload URL in X-Goog-Upload-URL header
            if (!initiateResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var uploadUrls))
                throw new InvalidOperationException("No X-Goog-Upload-URL in response headers");

            string? uploadUrl = null;
            foreach (var candidateUrl in uploadUrls)
            {
                if (!string.IsNullOrEmpty(candidateUrl))
                {
                    uploadUrl = candidateUrl;
                    break;
                }
            }

            if (string.IsNullOrEmpty(uploadUrl))
                throw new InvalidOperationException("Empty X-Goog-Upload-URL in response headers");
            Log.Debug($"[Scotty] Got upload URL: {uploadUrl[..Math.Min(100, uploadUrl.Length)]}...");

            // STEP 2: Upload binary data
            using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl);

            uploadRequest.Headers.Add("X-Goog-Upload-Offset", "0");
            uploadRequest.Headers.Add("X-Goog-Upload-Command", "upload, finalize");

            uploadRequest.Content = new ByteArrayContent(imageData);
            uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded")
            {
                CharSet = "utf-8"
            };

            using var uploadResponse = await http.SendAsync(uploadRequest, ct).ConfigureAwait(false);
            uploadResponse.EnsureSuccessStatusCode();

            // Parse JSON response to extract blobId
            using var uploadStream = await uploadResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var uploadResult = await Json.ParseAsync(uploadStream, ct).ConfigureAwait(false);

            // Response format: {"encryptedBlobId": "..."}
            var blobId = uploadResult.GetPropertyOrNull("encryptedBlobId")?.GetStringOrNull();
            if (string.IsNullOrEmpty(blobId))
            {
                Log.Error("[Scotty] No encryptedBlobId in upload response");
                return false;
            }

            Log.Debug($"[Scotty] Got blobId: {blobId[..Math.Min(50, blobId.Length)]}...");

            // STEP 3: Apply thumbnail via edit_playlist API
            var applyRoot = await PostAsync(EditPlaylistEndpoint, writer =>
            {
                writer.WriteString("playlistId", SanitizePlaylistId(playlistId));

                writer.WriteStartArray("actions");
                writer.WriteStartObject();

                writer.WriteString("action", "ACTION_SET_CUSTOM_THUMBNAIL");

                writer.WriteStartObject("addedCustomThumbnail");

                writer.WriteStartObject("imageKey");
                writer.WriteString("type", "PLAYLIST_IMAGE_TYPE_CUSTOM_THUMBNAIL");
                writer.WriteString("name", "studio_square_thumbnail");
                writer.WriteEndObject(); // imageKey

                writer.WriteString("playlistScottyEncryptedBlobId", blobId);

                writer.WriteEndObject(); // addedCustomThumbnail

                writer.WriteEndObject(); // action
                writer.WriteEndArray(); // actions
            }, ct).ConfigureAwait(false);

            CheckStatus(applyRoot);

            Log.Info($"[Scotty] Thumbnail uploaded successfully for playlist {playlistId} ({imageData.Length} bytes)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[Scotty] Upload failed for playlist {playlistId}: {ex.Message}");
            throw;
        }
    }

    #endregion
}