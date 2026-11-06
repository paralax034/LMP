using System.Runtime.CompilerServices;
using System.Text.Json;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Extension methods for low-allocation parsing of <see cref="JsonElement"/>.
/// </summary>
internal static class JsonExtensions
{
    extension(JsonElement element)
    {
        /// <summary>
        /// Extracts VisitorData from the <c>responseContext</c> structure.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string? GetVisitorData() =>
            element.GetPropertyOrNull("responseContext"u8)
                   ?.GetPropertyOrNull("visitorData"u8)
                   ?.GetStringOrNull();

        /// <summary>
        /// Returns a property by string name, or <see langword="null"/> if it is missing or null.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsonElement? GetPropertyOrNull(string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            if (element.TryGetProperty(propertyName, out var result)
                && result.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                return result;
            }

            return null;
        }

        /// <summary>
        /// Returns a property by UTF-8 name without allocations, or <see langword="null"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsonElement? GetPropertyOrNull(ReadOnlySpan<byte> utf8PropertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            if (element.TryGetProperty(utf8PropertyName, out var result)
                && result.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                return result;
            }

            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool? GetBooleanOrNull() =>
            element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string? GetStringOrNull() =>
            element.ValueKind == JsonValueKind.String ? element.GetString() : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int? GetInt32OrNull() =>
            element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var result)
                ? result
                : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long? GetInt64OrNull() =>
            element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var result)
                ? result
                : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double? GetDoubleOrNull() =>
            element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var result)
                ? result
                : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsonElement.ArrayEnumerator? EnumerateArrayOrNull() =>
            element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsonElement.ArrayEnumerator EnumerateArrayOrEmpty() =>
            element.EnumerateArrayOrNull() ?? default;

        /// <summary>
        /// Returns an array element by index in O(1), or <see langword="null"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsonElement? GetArrayElementOrNull(int index)
        {
            if (element.ValueKind != JsonValueKind.Array)
                return null;

            var len = element.GetArrayLength();
            if (index < 0 || index >= len)
                return null;

            return element[index];
        }

        /// <summary>
        /// Returns the first element of the array, or <see langword="null"/> if the array is empty.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsonElement? GetFirstArrayElementOrNull()
        {
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
                return null;

            return element[0];
        }

        /// <summary>
        /// Recursively collects all nested properties named <paramref name="propertyName"/> into the <paramref name="results"/> list.
        /// </summary>
        public void EnumerateDescendantProperties(string propertyName, List<JsonElement> results)
        {
            var property = element.GetPropertyOrNull(propertyName);
            if (property is not null)
                results.Add(property.Value);

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                    child.EnumerateDescendantProperties(propertyName, results);
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                    prop.Value.EnumerateDescendantProperties(propertyName, results);
            }
        }

        /// <summary>
        /// Searches for the first occurrence of property <paramref name="propertyName"/> in the tree with early exit.
        /// </summary>
        public JsonElement? FindFirstDescendantProperty(string propertyName)
        {
            var property = element.GetPropertyOrNull(propertyName);
            if (property is not null)
                return property.Value;

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                {
                    var found = child.FindFirstDescendantProperty(propertyName);
                    if (found is not null) return found;
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    var found = prop.Value.FindFirstDescendantProperty(propertyName);
                    if (found is not null) return found;
                }
            }

            return null;
        }

        /// <summary>
        /// Searches for the first occurrence of UTF-8 property <paramref name="utf8PropertyName"/> in the tree with early exit.
        /// </summary>
        public JsonElement? FindFirstDescendantProperty(ReadOnlySpan<byte> utf8PropertyName)
        {
            var property = element.GetPropertyOrNull(utf8PropertyName);
            if (property is not null)
                return property.Value;

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                {
                    var found = child.FindFirstDescendantProperty(utf8PropertyName);
                    if (found is not null) return found;
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    var found = prop.Value.FindFirstDescendantProperty(utf8PropertyName);
                    if (found is not null) return found;
                }
            }

            return null;
        }
    }
}