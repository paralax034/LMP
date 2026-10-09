using System.Collections;

namespace Avalonia.Controls
{
    internal class UniqueIdElementPool : IEnumerable<KeyValuePair<string, Control>>
    {
        private readonly Dictionary<string, Control> _elementMap = new Dictionary<string, Control>();
        private readonly ItemsRepeater _owner;

        public UniqueIdElementPool(ItemsRepeater owner) => _owner = owner;

        public void Add(Control element)
        {
            var virtInfo = ItemsRepeater.GetVirtualizationInfo(element);
            var key = virtInfo.UniqueId!;

            if (_elementMap.ContainsKey(key))
            {
                throw new InvalidOperationException($"The unique id provided ({key}) is not unique.");
            }

            _elementMap.Add(key, element);
        }

        public Control? Remove(int index)
        {
            _ = index;
            _ = _owner;
            return null;
        }

        public void Clear()
        {
            _elementMap.Clear();
        }

        public IEnumerator<KeyValuePair<string, Control>> GetEnumerator() => _elementMap.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}