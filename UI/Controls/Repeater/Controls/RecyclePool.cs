using System.Runtime.CompilerServices;
using Avalonia.Controls.Templates;

namespace Avalonia.Controls
{
    /// <summary>
    /// High-performance, zero-allocation element recycle pool optimized for high-refresh-rate virtualized scrolling.
    /// </summary>
    public sealed class RecyclePool
    {
        private const int DefaultMaxCapacity = 100;

        internal static readonly AttachedProperty<IDataTemplate> OriginTemplateProperty =
            AvaloniaProperty.RegisterAttached<RecyclePool, Control, IDataTemplate>("OriginTemplate");

        internal static readonly AttachedProperty<string> ReuseKeyProperty =
            AvaloniaProperty.RegisterAttached<RecyclePool, Control, string>("ReuseKey", string.Empty);

        private static readonly ConditionalWeakTable<IDataTemplate, RecyclePool> s_pools = new();

        private readonly Stack<Control> _fastPool = new(DefaultMaxCapacity);
        private readonly Dictionary<string, Stack<Control>> _keyedPools = new(StringComparer.Ordinal);

        public int MaxCapacity { get; set; } = DefaultMaxCapacity;

        public static RecyclePool? GetPoolInstance(IDataTemplate dataTemplate)
        {
            s_pools.TryGetValue(dataTemplate, out var result);
            return result;
        }

        public static void SetPoolInstance(IDataTemplate dataTemplate, RecyclePool value) =>
            s_pools.AddOrUpdate(dataTemplate, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PutElement(Control element, string key, Control? owner)
        {
            _ = owner;

            if (string.IsNullOrEmpty(key))
            {
                if (_fastPool.Count < MaxCapacity)
                {
                    _fastPool.Push(element);
                }
                return;
            }

            if (!_keyedPools.TryGetValue(key, out var pool))
            {
                pool = new Stack<Control>(MaxCapacity);
                _keyedPools[key] = pool;
            }

            if (pool.Count < MaxCapacity)
            {
                pool.Push(element);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Control? TryGetElement(string key, Control? owner)
        {
            _ = owner;

            if (string.IsNullOrEmpty(key))
            {
                return _fastPool.Count > 0 ? _fastPool.Pop() : null;
            }

            if (_keyedPools.TryGetValue(key, out var pool) && pool.Count > 0)
            {
                return pool.Pop();
            }

            return null;
        }

        internal string GetReuseKey(Control element) => element.GetValue(ReuseKeyProperty);
        internal void SetReuseKey(Control element, string value) => element.SetValue(ReuseKeyProperty, value);
    }
}