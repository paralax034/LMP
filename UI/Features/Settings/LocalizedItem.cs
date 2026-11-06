namespace LMP.UI.Features.Settings;

/// <summary>
/// Static factory for creating localized enum collections without redundant generic parameters.
/// </summary>
public static class LocalizedItem
{
    /// <summary>
    /// Automatically builds an immutable list of localized items for the specified enum.
    /// Guarantees correct type inference by the C# compiler.
    /// </summary>
    public static IReadOnlyList<LocalizedItem<TEnum>> CreateList<TEnum>(
        string keyPrefix,
        Func<TEnum, string>? customKeyResolver = null) where TEnum : struct, Enum
    {
        var values = Enum.GetValues<TEnum>();
        var items = new LocalizedItem<TEnum>[values.Length];

        for (int i = 0; i < values.Length; i++)
        {
            var val = values[i];
            string key = customKeyResolver != null
                ? customKeyResolver(val)
                : string.Concat(keyPrefix, val.ToString());

            items[i] = new LocalizedItem<TEnum>(val, LocalizationService.Instance[key]);
        }

        return items;
    }
}

/// <summary>
/// Wrapper around an arbitrary value with a name for display in a ComboBox.
/// <para>
/// ToString() returns Name — ComboBox calls it directly,
/// without creating DataTemplate containers. This eliminates the memory leak
/// from DisplayMemberBinding → ControlTemplate binding → PointerDeferredContent.
/// </para>
/// </summary>
public sealed class LocalizedItem<T>(T value, string name)
{
    public T Value { get; } = value;
    public string Name { get; } = name;

    public override string ToString() => Name;
}

public static class LocalizedItemExtensions
{
    /// <summary>
    /// Finds an item by value without creating LINQ closures.
    /// </summary>
    public static LocalizedItem<T> FindByValue<T>(
        this IReadOnlyList<LocalizedItem<T>> list,
        T value,
        int fallbackIndex = 0)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(list[i].Value, value))
                return list[i];
        }

        return list.Count > 0
            ? list[Math.Clamp(fallbackIndex, 0, list.Count - 1)]
            : default!;
    }
}
