using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace LMP.Core.Services;

/// <summary>
/// Discord local IPC wire opcodes.
/// </summary>
internal enum DiscordIpcOpcode : int
{
    Handshake = 0,
    Frame = 1,
    Close = 2,
    Ping = 3,
    Pong = 4
}

/// <summary>
/// Result of a Discord IPC handshake.
/// </summary>
internal readonly record struct DiscordHandshakeResult(
    bool Success,
    string? UserDisplayName,
    string? ErrorMessage);

/// <summary>
/// Pure C#, Native AOT-safe cross-platform Discord IPC transport client.
/// Uses <see cref="NamedPipeClientStream"/> on Windows (<c>discord-ipc-0..9</c>)
/// and Unix Domain Sockets on Linux/macOS (native, Flatpak, and Snap paths).
/// All JSON serialization runs through SourceGen <see cref="AppJsonContext"/> with pooled buffers.
/// </summary>
internal sealed class DiscordIpcClient : IAsyncDisposable
{
    private const int MaxPipeSlots = 10;
    private const int HeaderSize = 8;
    private const int MaxFramePayloadBytes = 256 * 1024;
    private const int PerPipeConnectTimeoutMs = 300;
    private const int HandshakeResponseTimeoutMs = 5_000;
    private const int CommandResponseTimeoutMs = 2_500;
    private const int PingResponseTimeoutMs = 2_000;

    private static readonly ReadOnlyMemory<byte> EmptyJsonPayload = "{}"u8.ToArray();

    private readonly Func<CancellationToken, ValueTask<Stream?>>? _transportFactoryOverride;
    private readonly ArrayBufferWriter<byte> _jsonBuffer = new(2048);
    private readonly byte[] _readHeaderBuffer = new byte[HeaderSize];

    private Stream? _stream;
    private long _nonceCounter;

    /// <summary>
    /// Gets whether an IPC pipe/socket is currently open and has completed the handshake.
    /// </summary>
    public bool IsConnected => Volatile.Read(ref _stream) != null;

    /// <summary>
    /// Initializes a new instance of <see cref="DiscordIpcClient"/>.
    /// </summary>
    /// <param name="transportFactoryOverride">
    /// Optional stream factory used by deterministic unit tests to bypass OS pipes/sockets.
    /// </param>
    public DiscordIpcClient(Func<CancellationToken, ValueTask<Stream?>>? transportFactoryOverride = null)
    {
        _transportFactoryOverride = transportFactoryOverride;
    }

    /// <summary>
    /// Discovers a running local Discord IPC endpoint, connects, and performs the v1 handshake.
    /// </summary>
    public async ValueTask<DiscordHandshakeResult> ConnectAsync(string clientId, CancellationToken ct)
    {
        await DisconnectAsync(sendCloseFrame: false, CancellationToken.None).ConfigureAwait(false);

        Stream? stream = _transportFactoryOverride != null
            ? await _transportFactoryOverride(ct).ConfigureAwait(false)
            : await OpenPlatformStreamAsync(ct).ConfigureAwait(false);

        if (stream == null)
            return new DiscordHandshakeResult(false, null, null);

        Interlocked.Exchange(ref _stream, stream);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(HandshakeResponseTimeoutMs);

            var handshake = new DiscordHandshakePayload
            {
                Version = 1,
                ClientId = clientId
            };

            await WriteSerializedFrameAsync(
                DiscordIpcOpcode.Handshake,
                handshake,
                AppJsonContext.DefaultCompact.DiscordHandshakePayload,
                timeoutCts.Token).ConfigureAwait(false);

            while (true)
            {
                var (received, opcode, payloadLength, rentedPayload) =
                    await ReadFrameAsync(timeoutCts.Token).ConfigureAwait(false);

                if (!received)
                {
                    await DisconnectAsync(sendCloseFrame: false, CancellationToken.None).ConfigureAwait(false);
                    return new DiscordHandshakeResult(false, null, "IPC closed during handshake");
                }

                try
                {
                    if (opcode == DiscordIpcOpcode.Ping)
                    {
                        await WriteRawFrameAsync(
                            DiscordIpcOpcode.Pong,
                            rentedPayload.AsMemory(0, payloadLength),
                            timeoutCts.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (opcode == DiscordIpcOpcode.Close)
                    {
                        string? closeMsg = ExtractErrorMessage(rentedPayload.AsMemory(0, payloadLength));
                        await DisconnectAsync(sendCloseFrame: false, CancellationToken.None).ConfigureAwait(false);
                        return new DiscordHandshakeResult(false, null, closeMsg ?? "Handshake rejected by Discord");
                    }

                    if (opcode != DiscordIpcOpcode.Frame)
                        continue;

                    var result = ParseHandshakeFrame(rentedPayload.AsMemory(0, payloadLength));
                    if (!result.Success)
                        await DisconnectAsync(sendCloseFrame: false, CancellationToken.None).ConfigureAwait(false);

                    return result;
                }
                finally
                {
                    if (rentedPayload != null)
                        ArrayPool<byte>.Shared.Return(rentedPayload);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisconnectAsync(sendCloseFrame: false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await DisconnectAsync(sendCloseFrame: false, CancellationToken.None).ConfigureAwait(false);
            return new DiscordHandshakeResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Sends a <c>SET_ACTIVITY</c> frame (or clears presence when <paramref name="activity"/> is <see langword="null"/>)
    /// and drains the corresponding IPC response so the OS pipe buffer never stalls.
    /// </summary>
    public async ValueTask<bool> SetActivityAsync(DiscordActivity? activity, CancellationToken ct)
    {
        if (Volatile.Read(ref _stream) == null)
            return false;

        string nonce = Interlocked.Increment(ref _nonceCounter).ToString(CultureInfo.InvariantCulture);
        var frame = new DiscordRpcCommandFrame
        {
            Cmd = "SET_ACTIVITY",
            Args = new DiscordSetActivityArgs
            {
                Pid = Environment.ProcessId,
                Activity = activity
            },
            Nonce = nonce
        };

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(CommandResponseTimeoutMs);

            await WriteSerializedFrameAsync(
                DiscordIpcOpcode.Frame,
                frame,
                AppJsonContext.DefaultCompact.DiscordRpcCommandFrame,
                timeoutCts.Token).ConfigureAwait(false);

            return await DrainResponseAsync(expectedNonce: nonce, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"[DiscordRPC] IPC write/read failed: {ex.Message}");
            await DisconnectAsync(sendCloseFrame: false, CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Sends an IPC <c>PING</c> frame and awaits the <c>PONG</c> to detect dead connections while idle.
    /// </summary>
    public async ValueTask<bool> PingAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _stream) == null)
            return false;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PingResponseTimeoutMs);

            await WriteRawFrameAsync(DiscordIpcOpcode.Ping, EmptyJsonPayload, timeoutCts.Token).ConfigureAwait(false);

            while (true)
            {
                var (received, opcode, payloadLength, rentedPayload) =
                    await ReadFrameAsync(timeoutCts.Token).ConfigureAwait(false);

                if (!received)
                {
                    await DisconnectAsync(sendCloseFrame: false, CancellationToken.None).ConfigureAwait(false);
                    return false;
                }

                try
                {
                    if (opcode == DiscordIpcOpcode.Pong)
                        return true;

                    if (opcode == DiscordIpcOpcode.Ping)
                    {
                        await WriteRawFrameAsync(
                            DiscordIpcOpcode.Pong,
                            rentedPayload.AsMemory(0, payloadLength),
                            timeoutCts.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (opcode == DiscordIpcOpcode.Close)
                    {
                        await DisconnectAsync(sendCloseFrame: false, CancellationToken.None).ConfigureAwait(false);
                        return false;
                    }
                }
                finally
                {
                    if (rentedPayload != null)
                        ArrayPool<byte>.Shared.Return(rentedPayload);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await DisconnectAsync(sendCloseFrame: false, CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Optionally sends a <c>CLOSE</c> opcode and disposes the active IPC stream.
    /// </summary>
    public async ValueTask DisconnectAsync(bool sendCloseFrame, CancellationToken ct)
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream == null)
            return;

        if (sendCloseFrame)
        {
            try
            {
                using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                closeCts.CancelAfter(500);
                await WriteRawFrameToStreamAsync(stream, DiscordIpcOpcode.Close, EmptyJsonPayload, closeCts.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
        }

        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public ValueTask DisposeAsync() => DisconnectAsync(sendCloseFrame: true, CancellationToken.None);

    private async ValueTask<bool> DrainResponseAsync(string expectedNonce, CancellationToken ct)
    {

        while (true)
        {
            var (received, opcode, payloadLength, rentedPayload) =
                await ReadFrameAsync(ct).ConfigureAwait(false);

            if (!received)
            {
                await DisconnectAsync(sendCloseFrame: false, CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            try
            {
                if (opcode == DiscordIpcOpcode.Ping)
                {
                    await WriteRawFrameAsync(
                        DiscordIpcOpcode.Pong,
                        rentedPayload.AsMemory(0, payloadLength),
                        ct).ConfigureAwait(false);
                    continue;
                }

                if (opcode == DiscordIpcOpcode.Close)
                {
                    string? closeMsg = ExtractErrorMessage(rentedPayload.AsMemory(0, payloadLength));
                    Log.Warn($"[DiscordRPC] Discord closed IPC channel: {closeMsg ?? "unknown"}");
                    await DisconnectAsync(sendCloseFrame: false, CancellationToken.None).ConfigureAwait(false);
                    return false;
                }

                if (opcode != DiscordIpcOpcode.Frame)
                    continue;

                if (TryInspectResponseFrame(
                    rentedPayload.AsMemory(0, payloadLength),
                    expectedNonce,
                    out bool matchedNonce,
                    out string? errorMessage))
                {
                    if (errorMessage != null)
                        Log.Warn($"[DiscordRPC] SET_ACTIVITY rejected: {errorMessage}");

                    if (matchedNonce)
                        return true;
                }
            }
            finally
            {
                if (rentedPayload != null)
                    ArrayPool<byte>.Shared.Return(rentedPayload);
            }
        }
    }

    private ValueTask WriteSerializedFrameAsync<T>(
        DiscordIpcOpcode opcode,
        T payload,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        _jsonBuffer.ResetWrittenCount();
        using (var writer = new Utf8JsonWriter(_jsonBuffer))
        {
            JsonSerializer.Serialize(writer, payload, typeInfo);
        }

        return WriteRawFrameAsync(opcode, _jsonBuffer.WrittenMemory, ct);
    }

    private ValueTask WriteRawFrameAsync(
        DiscordIpcOpcode opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        var stream = Volatile.Read(ref _stream) ?? throw new IOException("Discord IPC stream is not connected.");
        return WriteRawFrameToStreamAsync(stream, opcode, payload, ct);
    }

    private static async ValueTask WriteRawFrameToStreamAsync(
        Stream stream,
        DiscordIpcOpcode opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        int totalLength = HeaderSize + payload.Length;
        byte[] packet = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), (int)opcode);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), payload.Length);
            payload.Span.CopyTo(packet.AsSpan(HeaderSize, payload.Length));

            await stream.WriteAsync(packet.AsMemory(0, totalLength), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    private async ValueTask<(bool Received, DiscordIpcOpcode Opcode, int PayloadLength, byte[]? RentedPayload)>
        ReadFrameAsync(CancellationToken ct)
    {
        var stream = Volatile.Read(ref _stream);
        if (stream == null)
            return (false, default, 0, null);

        if (!await ReadExactOrEofAsync(stream, _readHeaderBuffer.AsMemory(0, HeaderSize), ct).ConfigureAwait(false))
            return (false, default, 0, null);

        int rawOpcode = BinaryPrimitives.ReadInt32LittleEndian(_readHeaderBuffer.AsSpan(0, 4));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(_readHeaderBuffer.AsSpan(4, 4));

        if (payloadLength < 0 || payloadLength > MaxFramePayloadBytes)
            throw new InvalidDataException($"Invalid Discord IPC payload length: {payloadLength}");

        if (payloadLength == 0)
            return (true, (DiscordIpcOpcode)rawOpcode, 0, null);

        byte[] rented = ArrayPool<byte>.Shared.Rent(payloadLength);
        try
        {
            if (!await ReadExactOrEofAsync(stream, rented.AsMemory(0, payloadLength), ct).ConfigureAwait(false))
            {
                ArrayPool<byte>.Shared.Return(rented);
                return (false, default, 0, null);
            }

            return (true, (DiscordIpcOpcode)rawOpcode, payloadLength, rented);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    private static async ValueTask<bool> ReadExactOrEofAsync(Stream stream, Memory<byte> target, CancellationToken ct)
    {
        int offset = 0;
        while (offset < target.Length)
        {
            int read = await stream.ReadAsync(target[offset..], ct).ConfigureAwait(false);
            if (read == 0)
                return false;

            offset += read;
        }

        return true;
    }

    private static DiscordHandshakeResult ParseHandshakeFrame(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
            return new DiscordHandshakeResult(false, null, "Empty handshake response");

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("evt"u8, out var evtProp) && evtProp.ValueKind == JsonValueKind.String)
            {
                if (evtProp.ValueEquals("READY"u8))
                {
                    string? userName = null;
                    if (root.TryGetProperty("data"u8, out var dataProp)
                        && dataProp.TryGetProperty("user"u8, out var userProp))
                    {
                        if (userProp.TryGetProperty("global_name"u8, out var globalProp)
                            && globalProp.ValueKind == JsonValueKind.String)
                        {
                            userName = globalProp.GetString();
                        }

                        if (string.IsNullOrWhiteSpace(userName)
                            && userProp.TryGetProperty("username"u8, out var nameProp)
                            && nameProp.ValueKind == JsonValueKind.String)
                        {
                            userName = nameProp.GetString();
                        }
                    }

                    return new DiscordHandshakeResult(true, userName, null);
                }

                if (evtProp.ValueEquals("ERROR"u8))
                {
                    string? message = ExtractErrorMessageFromRoot(root);
                    return new DiscordHandshakeResult(false, null, message ?? "Discord returned ERROR");
                }
            }

            return new DiscordHandshakeResult(true, null, null);
        }
        catch (Exception ex)
        {
            return new DiscordHandshakeResult(false, null, ex.Message);
        }
    }

    private static bool TryInspectResponseFrame(
        ReadOnlyMemory<byte> payload,
        string expectedNonce,
        out bool matchedNonce,
        out string? errorMessage)
    {
        matchedNonce = false;
        errorMessage = null;

        if (payload.IsEmpty)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("nonce"u8, out var nonceProp)
                && nonceProp.ValueKind == JsonValueKind.String
                && string.Equals(nonceProp.GetString(), expectedNonce, StringComparison.Ordinal))
            {
                matchedNonce = true;
            }

            if (root.TryGetProperty("evt"u8, out var evtProp)
                && evtProp.ValueKind == JsonValueKind.String
                && evtProp.ValueEquals("ERROR"u8))
            {
                errorMessage = ExtractErrorMessageFromRoot(root) ?? "Discord RPC error";
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractErrorMessage(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            return ExtractErrorMessageFromRoot(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractErrorMessageFromRoot(JsonElement root)
    {
        if (root.TryGetProperty("message"u8, out var topMsg) && topMsg.ValueKind == JsonValueKind.String)
            return topMsg.GetString();

        if (root.TryGetProperty("data"u8, out var dataProp)
            && dataProp.TryGetProperty("message"u8, out var nestedMsg)
            && nestedMsg.ValueKind == JsonValueKind.String)
        {
            return nestedMsg.GetString();
        }

        return null;
    }

    #region Platform Discovery

    private static ValueTask<Stream?> OpenPlatformStreamAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return OpenWindowsPipeAsync(ct);

        return OpenUnixSocketAsync(ct);
    }

    private static async ValueTask<Stream?> OpenWindowsPipeAsync(CancellationToken ct)
    {
        // Fast probe of the local named-pipe namespace avoids 10 * 300ms connect timeouts when Discord is closed.
        List<string>? discoveredPipes = null;
        bool enumerationSucceeded = false;

        try
        {
            foreach (string fullPath in Directory.EnumerateFiles(@"\\.\pipe\", "discord-ipc-*"))
            {
                string pipeName = Path.GetFileName(fullPath);
                if (!string.IsNullOrEmpty(pipeName))
                {
                    discoveredPipes ??= new List<string>(2);
                    discoveredPipes.Add(pipeName);
                }
            }

            enumerationSucceeded = true;
        }
        catch
        {
        }

        if (enumerationSucceeded)
        {
            if (discoveredPipes == null || discoveredPipes.Count == 0)
                return null;

            discoveredPipes.Sort(StringComparer.Ordinal);
            for (int i = 0; i < discoveredPipes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var pipe = await TryConnectWindowsPipeAsync(discoveredPipes[i], PerPipeConnectTimeoutMs, ct)
                    .ConfigureAwait(false);
                if (pipe != null)
                    return pipe;
            }

            return null;
        }

        for (int slot = 0; slot < MaxPipeSlots; slot++)
        {
            ct.ThrowIfCancellationRequested();
            var pipe = await TryConnectWindowsPipeAsync($"discord-ipc-{slot}", 120, ct).ConfigureAwait(false);
            if (pipe != null)
                return pipe;
        }

        return null;
    }

    private static async ValueTask<Stream?> TryConnectWindowsPipeAsync(
        string pipeName,
        int timeoutMs,
        CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: pipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            return pipe;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    private static async ValueTask<Stream?> OpenUnixSocketAsync(CancellationToken ct)
    {
        string?[] candidateDirs =
        [
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            Environment.GetEnvironmentVariable("TMPDIR"),
            Environment.GetEnvironmentVariable("TMP"),
            Environment.GetEnvironmentVariable("TEMP"),
            "/tmp"
        ];

        string[] subPrefixes =
        [
            string.Empty,
            "app/com.discordapp.Discord",
            "snap.discord"
        ];

        for (int d = 0; d < candidateDirs.Length; d++)
        {
            string? baseDir = candidateDirs[d];
            if (string.IsNullOrWhiteSpace(baseDir))
                continue;

            bool duplicate = false;
            for (int prev = 0; prev < d; prev++)
            {
                if (string.Equals(candidateDirs[prev], baseDir, StringComparison.Ordinal))
                {
                    duplicate = true;
                    break;
                }
            }

            if (duplicate)
                continue;

            for (int p = 0; p < subPrefixes.Length; p++)
            {
                string dir = subPrefixes[p].Length == 0
                    ? baseDir
                    : Path.Combine(baseDir, subPrefixes[p]);

                for (int slot = 0; slot < MaxPipeSlots; slot++)
                {
                    ct.ThrowIfCancellationRequested();
                    string socketPath = Path.Combine(dir, $"discord-ipc-{slot}");
                    if (!Path.Exists(socketPath))
                        continue;

                    var stream = await TryConnectUnixSocketAsync(socketPath, PerPipeConnectTimeoutMs, ct)
                        .ConfigureAwait(false);
                    if (stream != null)
                        return stream;
                }
            }
        }

        return null;
    }

    private static async ValueTask<Stream?> TryConnectUnixSocketAsync(
        string socketPath,
        int timeoutMs,
        CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeoutCts.Token)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw;
        }
        catch
        {
            socket.Dispose();
            return null;
        }
    }

    #endregion
}