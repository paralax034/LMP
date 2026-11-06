namespace LMP.Core.ViewModels;

/// <summary>
/// Interface for ViewModels supporting text filtering.
/// </summary>
public interface IFilterable
{
    /// <summary>
    /// Text query for local filtering.
    /// </summary>
    string FilterQuery { get; set; }

    /// <summary>
    /// Localization service for UI.
    /// </summary>
    LocalizationService L { get; }
}