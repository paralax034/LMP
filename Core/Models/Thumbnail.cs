using System.Diagnostics.CodeAnalysis;

namespace LMP.Core.Models;

/// <summary>
/// Thumbnail image.
/// </summary>
public partial class Thumbnail(string url, Resolution resolution)
{
    /// <summary>
    /// Thumbnail URL.
    /// </summary>
    public string Url { get; } = url;

    /// <summary>
    /// Thumbnail resolution.
    /// </summary>
    public Resolution Resolution { get; } = resolution;

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public override string ToString() => $"Thumbnail ({Resolution})";
}