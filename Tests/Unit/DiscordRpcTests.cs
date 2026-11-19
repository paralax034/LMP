using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Deterministic unit tests for the Native AOT Discord RPC IPC transport, SourceGen serialization,
/// snowflake validation, UTF-8 safe field truncation, and Rich Presence payload construction.
/// </summary>
public static class DiscordRpcTests
{
    [TestMethod(TestCategory.Unit, "DiscordRpc ClientId Validation (0 Alloc)", Group = TestGroups.System, Order = 1)]
    public static Task TestClientIdValidationZeroAllocAsync()
    {
        TestAssert.True(DiscordRpcService.IsValidClientId("12345678901234567"), "17-digit snowflake must be valid");
        TestAssert.True(DiscordRpcService.IsValidClientId("1335262308222111815"), "19-digit snowflake must be valid");
        TestAssert.True(DiscordRpcService.IsValidClientId(" 1335262308222111815 "), "Whitespace-padded snowflake must be valid");
        TestAssert.False(DiscordRpcService.IsValidClientId(""), "Empty string must be rejected");
        TestAssert.False(DiscordRpcService.IsValidClientId("123456"), "Too short ID must be rejected");
        TestAssert.False(DiscordRpcService.IsValidClientId("12345678901234567890123"), "Too long ID must be rejected");
        TestAssert.False(DiscordRpcService.IsValidClientId("133526230822211181a"), "Non-digit characters must be rejected");

        TestAssert.ZeroAlloc(() =>
        {
            _ = DiscordRpcService.IsValidClientId("1335262308222111815");
            _ = DiscordRpcService.IsValidClientId("invalid-id");
        }, warmup: 5, iterations: 1000);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "DiscordRpc UTF-8 Normalization & Safe Truncation", Group = TestGroups.System, Order = 2)]
    public static Task TestTextNormalizationAsync()
    {
        TestAssert.Null(DiscordRpcService.NormalizeDiscordText("   ", 128), "Whitespace-only text must normalize to null");

        // Single ASCII byte must be padded to >= 2 UTF-8 bytes for Discord schema compliance
        string? singleChar = DiscordRpcService.NormalizeDiscordText("A", 128);
        TestAssert.NotNull(singleChar);
        TestAssert.True(Encoding.UTF8.GetByteCount(singleChar) >= 2, "1-byte field must be padded to >= 2 UTF-8 bytes");

        // Multi-byte Cyrillic + Emoji string must truncate cleanly within exact byte limit without broken surrogates
        string longCyrillicEmoji = new string('Я', 80) + "🎵🎶🔥";
        string? truncated = DiscordRpcService.NormalizeDiscordText(longCyrillicEmoji, 128);
        TestAssert.NotNull(truncated);
        TestAssert.True(Encoding.UTF8.GetByteCount(truncated) <= 128, "Truncated UTF-8 byte count must be <= 128");
        TestAssert.True(truncated.EndsWith('…'), "Truncated field must end with ellipsis");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "DiscordRpc Duplicate Author Prefix Stripping", Group = TestGroups.System, Order = 3)]
    public static Task TestStripDuplicateAuthorPrefixAsync()
    {
        TestAssert.True(DiscordRpcService.IsValidClientId(DiscordRpcService.DefaultClientId),
            "Bundled DefaultClientId must be a valid snowflake");

        TestAssert.Equal("拷問の時間",
            DiscordRpcService.StripDuplicateAuthorPrefix("Rory in early 20s - 拷問の時間", "Rory in early 20s"));
        TestAssert.Equal("拷問の時間",
            DiscordRpcService.StripDuplicateAuthorPrefix("Rory in early 20s — 拷問の時間", "Rory in early 20s - Topic"));
        TestAssert.Equal("Some Artist - Song Name",
            DiscordRpcService.StripDuplicateAuthorPrefix("Some Artist - Song Name", "Trap Nation"),
            "Reposter channel name must never alter unrelated titles");
        TestAssert.Equal("Re-Zero",
            DiscordRpcService.StripDuplicateAuthorPrefix("Re-Zero", "Re"),
            "Hyphen inside a word without spaces must not be stripped");
        TestAssert.Equal("Artist - A",
            DiscordRpcService.StripDuplicateAuthorPrefix("Artist - A", "Artist"),
            "Too-short remainder (< 2 chars) must preserve original title");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "DiscordRpc Activity Payload Builder & Modes", Group = TestGroups.System, Order = 4)]
    public static Task TestBuildActivityModesAsync()
    {
        var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        var baseInput = new DiscordPresenceInput(
            Enabled: true,
            ClientId: DiscordRpcService.DefaultClientId,
            ActivityType: DiscordRpcActivityType.Listening,
            StatusDisplay: DiscordRpcStatusDisplay.TrackTitle,
            TimestampMode: DiscordRpcTimestampMode.ProgressBar,
            DetailsFormat: DiscordRpcDetailsFormat.TrackTitle,
            PausedBehavior: DiscordRpcPausedBehavior.ShowPaused,
            ShowCoverArt: true,
            ShowTrackButton: true,
            ShowGitHubButton: true,
            ShowPlaylistName: true,
            ShowPublicPlaylistLink: true,
            ShowLikeStatus: true,
            ShowRepeatMode: true,
            ShowShuffleMode: true,
            ShowQueuePosition: true,
            ShowListenerChannel: false,
            ShowIdleStatus: false,
            StripAuthorFromTitle: true,
            BadgeStyle: DiscordRpcBadgeStyle.PublicPlaylistCover,
            CustomBadgeUrl: null,
            TrackId: "yt_dQw4w9WgXcQ",
            RawVideoId: "dQw4w9WgXcQ",
            TrackTitle: "Rick Astley - Never Gonna Give You Up",
            TrackAuthor: "Rick Astley",
            ChannelId: "UCuAXFkgsw1L7xaCfnd5JJOw",
            ThumbnailUrl: "https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg",
            IsLiked: true,
            RepeatMode: RepeatMode.All,
            ShuffleEnabled: true,
            QueueIndex: 0,
            QueueCount: 31,
            Position: TimeSpan.FromSeconds(45),
            Duration: TimeSpan.FromSeconds(213),
            PlaybackState: PlaybackState.Playing,
            PlaylistName: "entropy",
            PublicPlaylistUrl: "https://www.youtube.com/playlist?list=PLpublic123",
            PublicPlaylistThumbnailUrl: "https://i.ytimg.com/vi/playlistcover/hqdefault.jpg",
            ListenerHandle: "@paralax034",
            ListenerChannelUrl: "https://www.youtube.com/@paralax034",
            ListenerAvatarUrl: "https://yt3.ggpht.com/avatar.jpg",
            CurrentPage: DiscordNavigationPage.Library,
            ViewingPlaylistName: null,
            ViewingPlaylistUrl: null,
            ViewingPlaylistThumbnailUrl: null,
            IdleSinceUtc: now.AddMinutes(-2),
            CapturedAtUtc: now);

        // 1. Playing: monochrome glyphs grouped cleanly, no playlist duplication, public playlist badge & max 2 buttons
        var playing = DiscordRpcService.BuildActivity(baseInput);
        TestAssert.NotNull(playing);
        TestAssert.Equal((int)DiscordRpcActivityType.Listening, playing.Type);
        TestAssert.Equal((int)DiscordRpcStatusDisplay.TrackTitle, playing.StatusDisplayType);
        TestAssert.Equal("Never Gonna Give You Up", playing.Details);
        TestAssert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", playing.DetailsUrl);
        TestAssert.Equal("Rick Astley", playing.State, "State must not duplicate playlist name shown in LargeText");
        TestAssert.Equal("https://www.youtube.com/channel/UCuAXFkgsw1L7xaCfnd5JJOw", playing.StateUrl);
        TestAssert.NotNull(playing.Timestamps);
        TestAssert.Equal(now.ToUnixTimeSeconds() - 45, playing.Timestamps.Start);
        TestAssert.Equal(now.ToUnixTimeSeconds() - 45 + 213, playing.Timestamps.End);
        TestAssert.NotNull(playing.Assets);
        TestAssert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", playing.Assets.LargeImage);
        TestAssert.Equal($"entropy · 1/31 · {DiscordRpcService.LikedGlyph} ↻ ⇄", playing.Assets.LargeText);
        TestAssert.Equal("https://i.ytimg.com/vi/playlistcover/hqdefault.jpg", playing.Assets.SmallImage);
        TestAssert.Equal("https://www.youtube.com/playlist?list=PLpublic123", playing.Assets.SmallUrl);
        TestAssert.NotNull(playing.Buttons);
        TestAssert.Equal(2, playing.Buttons.Count, "Discord allows at most 2 buttons");
        TestAssert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", playing.Buttons[0].Url);
        TestAssert.Equal("https://www.youtube.com/playlist?list=PLpublic123", playing.Buttons[1].Url);

        // 2. Unliked track + RepeatMode.One without shuffle shows outline heart and ↻1
        var unlikedRepeatOne = DiscordRpcService.BuildActivity(baseInput with
        {
            IsLiked = false,
            RepeatMode = RepeatMode.One,
            ShuffleEnabled = false,
            ShowQueuePosition = false
        });
        TestAssert.NotNull(unlikedRepeatOne);
        TestAssert.Equal("entropy · ♡ ↻1", unlikedRepeatOne.Assets?.LargeText);

        // 3. Minimal mode omits LargeText and SmallImage completely
        var minimal = DiscordRpcService.BuildActivity(baseInput with
        {
            ShowPlaylistName = false,
            ShowPublicPlaylistLink = false,
            ShowLikeStatus = false,
            ShowRepeatMode = false,
            ShowShuffleMode = false,
            ShowQueuePosition = false,
            BadgeStyle = DiscordRpcBadgeStyle.None
        });
        TestAssert.NotNull(minimal);
        TestAssert.NotNull(minimal.Assets);
        TestAssert.Null(minimal.Assets.LargeText, "Minimal mode must omit line 3");
        TestAssert.Null(minimal.Assets.SmallImage, "BadgeStyle.None must omit small_image");

        // 4. Listener channel opt-in + ListenerAvatar badge
        var listenerMode = DiscordRpcService.BuildActivity(baseInput with
        {
            ShowTrackButton = false,
            ShowPublicPlaylistLink = false,
            ShowListenerChannel = true,
            BadgeStyle = DiscordRpcBadgeStyle.ListenerAvatar
        });
        TestAssert.NotNull(listenerMode);
        TestAssert.Equal("https://yt3.ggpht.com/avatar.jpg", listenerMode.Assets?.SmallImage);
        TestAssert.Equal("https://www.youtube.com/@paralax034", listenerMode.Assets?.SmallUrl);
        TestAssert.NotNull(listenerMode.Buttons);
        TestAssert.Equal("https://www.youtube.com/@paralax034", listenerMode.Buttons[0].Url);

        // 5. Paused with ClearPresence returns null
        var pausedClear = DiscordRpcService.BuildActivity(baseInput with
        {
            PlaybackState = PlaybackState.Paused,
            PausedBehavior = DiscordRpcPausedBehavior.ClearPresence
        });
        TestAssert.Null(pausedClear, "Paused with ClearPresence must clear activity");

        // 6. Paused with ShowPaused omits ticking timestamps and formats crisp pause stamp (❚❚ mm:ss / mm:ss)
        var pausedKeep = DiscordRpcService.BuildActivity(baseInput with
        {
            PlaybackState = PlaybackState.Paused,
            PausedBehavior = DiscordRpcPausedBehavior.ShowPaused
        });
        TestAssert.NotNull(pausedKeep);
        TestAssert.Null(pausedKeep.Timestamps, "Paused activity must omit timestamps");
        TestAssert.Equal($"Rick Astley · {DiscordRpcService.PauseGlyph} 00:45 / 03:33", pausedKeep.State);

        // 7. ElapsedOnly omits End timestamp
        var elapsedOnly = DiscordRpcService.BuildActivity(baseInput with
        {
            TimestampMode = DiscordRpcTimestampMode.ElapsedOnly
        });
        TestAssert.NotNull(elapsedOnly);
        TestAssert.NotNull(elapsedOnly.Timestamps);
        TestAssert.True(elapsedOnly.Timestamps.Start.HasValue, "ElapsedOnly must include Start");
        TestAssert.False(elapsedOnly.Timestamps.End.HasValue, "ElapsedOnly must omit End");

        // 8. SourceGen serialization preserves explicit null Activity for clear-presence frames
        var clearFrame = new DiscordRpcCommandFrame
        {
            Cmd = "SET_ACTIVITY",
            Args = new DiscordSetActivityArgs { Pid = 1234, Activity = null },
            Nonce = "42"
        };
        string clearJson = JsonSerializer.Serialize(clearFrame, AppJsonContext.DefaultCompact.DiscordRpcCommandFrame);
        TestAssert.True(clearJson.Contains("\"activity\":null", StringComparison.Ordinal),
            "Clear presence frame must explicitly serialize \"activity\":null");

        // 9. Stopped state clears presence even when PausedBehavior == ShowPaused (and respects ShowIdleStatus)
        var stoppedClear = DiscordRpcService.BuildActivity(baseInput with
        {
            PlaybackState = PlaybackState.Stopped,
            PausedBehavior = DiscordRpcPausedBehavior.ShowPaused,
            ShowIdleStatus = false
        });
        TestAssert.Null(stoppedClear, "Stopped state must clear presence when ShowIdleStatus is false");

        var stoppedIdle = DiscordRpcService.BuildActivity(baseInput with
        {
            PlaybackState = PlaybackState.Stopped,
            PausedBehavior = DiscordRpcPausedBehavior.ShowPaused,
            ShowIdleStatus = true,
            CurrentPage = DiscordNavigationPage.Library
        });
        TestAssert.NotNull(stoppedIdle);
        TestAssert.Equal(LocalizationService.Instance["DiscordRpc_Idle_Library"], stoppedIdle.Details);
        TestAssert.Null(stoppedIdle.Assets, "Library idle must use default app icon");
        TestAssert.True(stoppedIdle.Timestamps?.Start.HasValue == true, "Idle activity must include Start timestamp");

        // 9b. Idle while viewing a public playlist replaces large image with playlist cover & clickable link
        var viewingPublicPlaylistIdle = DiscordRpcService.BuildActivity(baseInput with
        {
            PlaybackState = PlaybackState.Stopped,
            ShowIdleStatus = true,
            CurrentPage = DiscordNavigationPage.Playlist,
            ViewingPlaylistName = "late night zzz",
            ViewingPlaylistUrl = "https://www.youtube.com/playlist?list=PLpublic123",
            ViewingPlaylistThumbnailUrl = "https://i.ytimg.com/vi/cover/hqdefault.jpg"
        });
        TestAssert.NotNull(viewingPublicPlaylistIdle);
        TestAssert.Equal(LocalizationService.Instance["DiscordRpc_Idle_Playlist"], viewingPublicPlaylistIdle.Details);
        TestAssert.Equal("late night zzz", viewingPublicPlaylistIdle.State);
        TestAssert.Equal("https://i.ytimg.com/vi/cover/hqdefault.jpg", viewingPublicPlaylistIdle.Assets?.LargeImage);
        TestAssert.Equal("https://www.youtube.com/playlist?list=PLpublic123", viewingPublicPlaylistIdle.Assets?.LargeUrl);

        // 9c. Track with missing/overlong ThumbnailUrl falls back to stable public YouTube hqdefault cover
        var fallbackCover = DiscordRpcService.BuildActivity(baseInput with
        {
            ThumbnailUrl = null
        });
        TestAssert.NotNull(fallbackCover);
        TestAssert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", fallbackCover.Assets?.LargeImage);

        // 10. TrackAndArtist + PlaylistName + Paused appends pause stamp and binds StateUrl to public playlist (not artist channel)
        var pausedPlaylistFormatted = DiscordRpcService.BuildActivity(baseInput with
        {
            DetailsFormat = DiscordRpcDetailsFormat.TrackAndArtist,
            PlaybackState = PlaybackState.Paused,
            PausedBehavior = DiscordRpcPausedBehavior.ShowPaused
        });
        TestAssert.NotNull(pausedPlaylistFormatted);
        TestAssert.True(
            pausedPlaylistFormatted.State?.Contains(DiscordRpcService.PauseGlyph, StringComparison.Ordinal) == true,
            "Paused status must be appended even when DetailsFormat != TrackTitle with active playlist");
        TestAssert.Equal("https://www.youtube.com/playlist?list=PLpublic123", pausedPlaylistFormatted.StateUrl,
            "StateUrl must point to public playlist when DetailsFormat != TrackTitle shows playlist in State");

        // 11. TrackAndArtist without public playlist must not leak artist ChannelId into StateUrl
        var privatePlaylistFormatted = DiscordRpcService.BuildActivity(baseInput with
        {
            DetailsFormat = DiscordRpcDetailsFormat.ArtistAndTrack,
            PublicPlaylistUrl = null
        });
        TestAssert.NotNull(privatePlaylistFormatted);
        TestAssert.Null(privatePlaylistFormatted.StateUrl,
            "StateUrl must be null when State shows a private/local playlist instead of artist");

        // 12. External image URL validation enforces https:// and max 256 chars
        TestAssert.True(DiscordRpcService.IsValidExternalImageUrl("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg"));
        TestAssert.False(DiscordRpcService.IsValidExternalImageUrl("http://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg"),
            "Non-HTTPS URLs must be rejected");
        TestAssert.False(DiscordRpcService.IsValidExternalImageUrl("https://example.com/" + new string('a', 250)),
            "URLs longer than 256 characters must be rejected");

        // 13. Activity deduplication suppresses <=2s timestamp rounding jitter but detects real changes/seeks
        var jitterClone = DiscordRpcService.BuildActivity(baseInput with
        {
            CapturedAtUtc = now.AddSeconds(1)
        });
        TestAssert.NotNull(jitterClone);
        TestAssert.True(DiscordRpcService.AreActivitiesEquivalent(playing, jitterClone),
            "1-second timestamp rounding jitter must be treated as equivalent");

        var seeked = DiscordRpcService.BuildActivity(baseInput with
        {
            Position = TimeSpan.FromSeconds(75)
        });
        TestAssert.NotNull(seeked);
        TestAssert.False(DiscordRpcService.AreActivitiesEquivalent(playing, seeked),
            "Seek shifting timestamps by >2s must not be deduplicated");

        var toggledLike = DiscordRpcService.BuildActivity(baseInput with { IsLiked = false });
        TestAssert.NotNull(toggledLike);
        TestAssert.False(DiscordRpcService.AreActivitiesEquivalent(playing, toggledLike),
            "Metadata change in LargeText must not be deduplicated");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "DiscordIpcClient Handshake, SetActivity & Ping/Pong Wire Framing", Group = TestGroups.System, Order = 5)]
    public static async Task TestIpcWireProtocolAsync(CancellationToken ct)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var clientStream = new DuplexPipeStream(serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream());
        var serverStream = new DuplexPipeStream(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());

        var serverTask = Task.Run(async () =>
        {
            // 1. Expect HANDSHAKE
            var (op1, json1) = await ReadRawFrameAsync(serverStream, ct).ConfigureAwait(false);
            TestAssert.Equal(DiscordIpcOpcode.Handshake, op1);
            TestAssert.True(json1.Contains("1335262308222111815", StringComparison.Ordinal));

            await WriteRawFrameAsync(
                serverStream,
                DiscordIpcOpcode.Frame,
                "{\"cmd\":\"DISPATCH\",\"evt\":\"READY\",\"data\":{\"user\":{\"global_name\":\"LMP Tester\"}}}",
                ct).ConfigureAwait(false);

            // 2. Expect SET_ACTIVITY
            var (op2, json2) = await ReadRawFrameAsync(serverStream, ct).ConfigureAwait(false);
            TestAssert.Equal(DiscordIpcOpcode.Frame, op2);
            using var doc = JsonDocument.Parse(json2);
            string nonce = doc.RootElement.GetProperty("nonce").GetString()!;
            TestAssert.Equal("SET_ACTIVITY", doc.RootElement.GetProperty("cmd").GetString());

            await WriteRawFrameAsync(
                serverStream,
                DiscordIpcOpcode.Frame,
                $"{{\"cmd\":\"SET_ACTIVITY\",\"nonce\":\"{nonce}\",\"data\":{{}}}}",
                ct).ConfigureAwait(false);

            // 3. Expect PING -> reply PONG
            var (op3, _) = await ReadRawFrameAsync(serverStream, ct).ConfigureAwait(false);
            TestAssert.Equal(DiscordIpcOpcode.Ping, op3);
            await WriteRawFrameAsync(serverStream, DiscordIpcOpcode.Pong, "{}", ct).ConfigureAwait(false);

            // 4. Expect CLOSE on dispose
            var (op4, _) = await ReadRawFrameAsync(serverStream, ct).ConfigureAwait(false);
            TestAssert.Equal(DiscordIpcOpcode.Close, op4);
        }, ct);

        await using (var ipc = new DiscordIpcClient(_ => ValueTask.FromResult<Stream?>(clientStream)))
        {
            var handshake = await ipc.ConnectAsync("1335262308222111815", ct).ConfigureAwait(false);
            TestAssert.True(handshake.Success, $"Handshake failed: {handshake.ErrorMessage}");
            TestAssert.Equal("LMP Tester", handshake.UserDisplayName);

            bool updated = await ipc.SetActivityAsync(new DiscordActivity
            {
                Type = (int)DiscordRpcActivityType.Listening,
                Details = "Test Track",
                State = "Test Artist"
            }, ct).ConfigureAwait(false);
            TestAssert.True(updated, "SET_ACTIVITY must succeed");

            bool pong = await ipc.PingAsync(ct).ConfigureAwait(false);
            TestAssert.True(pong, "PING/PONG keep-alive must succeed");
        }

        await serverTask.ConfigureAwait(false);

        // Concurrent DisconnectAsync calls must be idempotent and never double-dispose or throw
        var p1 = new Pipe();
        var p2 = new Pipe();
        var s = new DuplexPipeStream(p2.Reader.AsStream(), p1.Writer.AsStream());
        await using var raceClient = new DiscordIpcClient(_ => ValueTask.FromResult<Stream?>(s));
        var d1 = raceClient.DisconnectAsync(sendCloseFrame: false, ct).AsTask();
        var d2 = raceClient.DisconnectAsync(sendCloseFrame: false, ct).AsTask();
        await Task.WhenAll(d1, d2).ConfigureAwait(false);
        TestAssert.False(raceClient.IsConnected, "Client must report disconnected after concurrent disconnects");
        await s.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task<(DiscordIpcOpcode Opcode, string Json)> ReadRawFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[8];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var opcode = (DiscordIpcOpcode)BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

        byte[] payload = new byte[length];
        if (length > 0)
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

        return (opcode, Encoding.UTF8.GetString(payload));
    }

    private static async Task WriteRawFrameAsync(Stream stream, DiscordIpcOpcode opcode, string json, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), (int)opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(8));
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private sealed class DuplexPipeStream(Stream readStream, Stream writeStream) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => writeStream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => writeStream.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => readStream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => readStream.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => writeStream.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => writeStream.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                readStream.Dispose();
                writeStream.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await readStream.DisposeAsync().ConfigureAwait(false);
            await writeStream.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}