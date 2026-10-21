using Avalonia;
using Avalonia.Data.Converters;

namespace OpenDelulu.ViewModels;

/// <summary>Small converters of the island map: the chosen time of day is outlined and fully opaque.</summary>
public static class MapConverters
{
    public static readonly IValueConverter SelectedBorder =
        new FuncValueConverter<bool, Thickness>(selected => selected ? new Thickness(3) : new Thickness(0));

    public static readonly IValueConverter SelectedOpacity =
        new FuncValueConverter<bool, double>(selected => selected ? 1.0 : 0.6);

    /// <summary>A CONFIG percentage (0..100) as an opacity.</summary>
    public static readonly IValueConverter Percent =
        new FuncValueConverter<int, double>(percent => System.Math.Clamp(percent, 0, 100) / 100.0);
}
