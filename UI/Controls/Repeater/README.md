# LMP High-Performance ItemsRepeater

This component is an optimized, zero-allocation native fork of the `ItemsRepeater` control, originally engineered by:

- **Microsoft WinUI Team** (https://github.com/microsoft/microsoft-ui-xaml)
- **Avalonia UI Contributors** (https://github.com/AvaloniaUI/Avalonia.Controls.ItemsRepeater)

### Motivations for this Fork in LMP (Lite Music Player):

1. **Native AOT & .NET 11 Readiness**: Elimination of dynamic reflection artifacts, dead WinRT/UWP compatibility layers, and unused layout engines (e.g. UniformGrid, WrapLayout).
2. **Zero-Alloc Hot Path**: Replaced LINQ-heavy `RecyclePool` with a high-throughput, non-allocating stack-based recycling cache ($O(1)$ operations, zero heap allocations per scroll frame).
3. **Flawless Collection Reordering**: Full native support for `NotifyCollectionChangedAction.Move` preventing item displacement glitches to `(-10000, -10000)`.

Special thanks and full credit to the original authors for the foundational architecture.
