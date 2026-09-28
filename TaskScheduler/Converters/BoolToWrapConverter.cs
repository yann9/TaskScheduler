using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TaskScheduler.Converters
{
    /// <summary>bool → TextWrapping（日志窗口的"折行"开关用）</summary>
    public sealed class BoolToWrapConverter : IValueConverter
    {
        /// <summary>单例：XAML 里用 {x:Static} 直接引用，省掉一处资源声明</summary>
        public static readonly BoolToWrapConverter Instance = new BoolToWrapConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? TextWrapping.Wrap : TextWrapping.NoWrap;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }
}
