using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Muesli.Windows.WinUI.Pages;

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isVisible = value is bool boolean && boolean;
        if (Invert) isVisible = !isVisible;
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
