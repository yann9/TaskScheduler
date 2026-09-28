using System;
using System.Globalization;
using System.Windows.Data;

namespace TaskScheduler.Converters
{
    /// <summary>DateTime? &lt;-&gt; "yyyy-MM-dd HH:mm" 双向转换</summary>
    public class DateTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime dt)
                return dt.ToString("yyyy-MM-dd HH:mm");
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = (value as string)?.Trim();

            // 非法输入 / 空输入都 DoNothing：
            // 旧实现非法输入返回 null → 一次性任务的 TargetTime 变成 null → 永不执行，
            // 而界面上没有任何提示。
            if (string.IsNullOrEmpty(s)) return System.Windows.Data.Binding.DoNothing;

            return TimeText.TryParseDateTime(s, out var dt)
                ? (object)dt
                : System.Windows.Data.Binding.DoNothing;
        }
    }
}
