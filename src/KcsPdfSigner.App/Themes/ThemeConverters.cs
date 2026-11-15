using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace KcsPdfSigner.App.Themes;

/// <summary>True when a ListView uses a GridView, so the theme can show column headers.</summary>
public sealed class IsGridViewConverter : IValueConverter
{
    public static readonly IsGridViewConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is GridView;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
