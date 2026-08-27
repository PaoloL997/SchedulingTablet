using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Color = Windows.UI.Color;

namespace Brembana;

internal static class ThemeHelper
{
    public static SolidColorBrush Brush(string key) =>
        Application.Current.Resources[key] as SolidColorBrush ?? new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));

    public static Color GetColor(string key) => Brush(key).Color;

    public static SolidColorBrush WithAlpha(string key, byte alpha)
    {
        var c = GetColor(key);
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }
}
