namespace LMP.UI.Features.Settings;

/// <summary>
/// Статическая фабрика для создания локализованных коллекций перечислений без избыточных generic-параметров.
/// </summary>
public static class LocalizedItem
{
    /// <summary>
    /// Автоматически строит неизменяемый список локализованных элементов для указанного перечисления.
    /// Гарантирует правильный вывод типов компилятором C#.
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
/// Обёртка над произвольным значением с именем для отображения в ComboBox.
/// <para>
/// ToString() возвращает Name — ComboBox вызывает его напрямую,
/// без создания DataTemplate-контейнеров. Это устраняет утечку памяти
/// от DisplayMemberBinding → ControlTemplate binding → PointerDeferredContent.
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
    /// Находит элемент по значению без создания замыканий LINQ.
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
