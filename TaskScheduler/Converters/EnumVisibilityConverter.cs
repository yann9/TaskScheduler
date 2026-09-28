using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TaskScheduler.Converters
{
    /// <summary>枚举值等于参数时返回 Visible，否则 Collapsed</summary>
    public class EnumVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return Visibility.Collapsed;
            return value.ToString() == parameter.ToString() ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
