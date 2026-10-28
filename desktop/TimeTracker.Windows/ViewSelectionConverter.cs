using System.Globalization;
using System.Windows.Data;

namespace TimeTracker.Windows;

public sealed class ViewSelectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int selected && int.TryParse(parameter?.ToString(), out var index) && selected == index;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
