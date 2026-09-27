using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Muesli.Windows.WinUI.Pages;

/// <summary>
/// Maps a repository-owned vendor mark filename (no extension) to a packaged image source.
/// Empty or unknown filenames yield null so the card falls back to the neutral glyph.
/// </summary>
public sealed partial class VendorLogoConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string file || string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        try
        {
            return new BitmapImage(new Uri($"ms-appx:///Assets/vendor/{file}.png"));
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("Vendor marks are one-way.");
}
