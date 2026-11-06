using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace LMP.Core.Helpers;

internal static class Json
{
    /// <summary>
    /// Extracts the first JSON object using Span — zero string allocation.
    /// </summary>
    public static ReadOnlySpan<char> ExtractSpan(ReadOnlySpan<char> source)
    {
        var depth = 0;
        var isInsideString = false;
        var startIndex = -1;

        for (int i = 0; i < source.Length; i++)
        {
            var ch = source[i];
            var prev = i > 0 ? source[i - 1] : default;

            if (ch == '"' && prev != '\\')
            {
                isInsideString = !isInsideString;
            }
            else if (ch == '{' && !isInsideString)
            {
                if (depth == 0) startIndex = i;
                depth++;
            }
            else if (ch == '}' && !isInsideString)
            {
                depth--;
            }

            if (depth == 0 && startIndex >= 0)
                return source.Slice(startIndex, i - startIndex + 1);
        }

        return startIndex >= 0 ? source[startIndex..] : source;
    }

    /// <summary>
    /// Backward compatibility — allocates a string.
    /// </summary>
    public static string Extract(string source)
    {
        var span = ExtractSpan(source.AsSpan());
        return span.ToString();
    }

    /// <summary>
    /// Parses a JSON string. Clones RootElement to detach from the document.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JsonElement Parse(string source)
    {
        // For strings, use the standard parser — it is optimized
        using var document = JsonDocument.Parse(source);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Parses JSON from UTF-8 bytes — zero-copy for HTTP responses.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JsonElement Parse(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Parses JSON from a byte array.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JsonElement Parse(byte[] utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Parses JSON from a stream — zero-copy for network responses.
    /// </summary>
    public static async ValueTask<JsonElement> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return document.RootElement.Clone();
    }

    public static JsonElement? TryParse(string source)
    {
        try
        {
            return Parse(source);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Encodes a string for JSON using ArrayPool.
    /// </summary>
    public static string Encode(string value)
    {
        if (!NeedsEncoding(value))
            return value;

        var maxLen = value.Length * 2;
        var buffer = ArrayPool<char>.Shared.Rent(maxLen);
        
        try
        {
            var pos = 0;
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\n': buffer[pos++] = '\\'; buffer[pos++] = 'n'; break;
                    case '\r': buffer[pos++] = '\\'; buffer[pos++] = 'r'; break;
                    case '\t': buffer[pos++] = '\\'; buffer[pos++] = 't'; break;
                    case '\\': buffer[pos++] = '\\'; buffer[pos++] = '\\'; break;
                    case '"': buffer[pos++] = '\\'; buffer[pos++] = '"'; break;
                    default: buffer[pos++] = c; break;
                }
            }
            return new string(buffer, 0, pos);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NeedsEncoding(string value)
    {
        foreach (var c in value)
        {
            if (c is '\n' or '\r' or '\t' or '\\' or '"')
                return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Serialize(string? value) =>
        value is not null ? $"\"{Encode(value)}\"" : "null";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Serialize(int? value) =>
        value is not null ? value.Value.ToString(CultureInfo.InvariantCulture) : "null";
}