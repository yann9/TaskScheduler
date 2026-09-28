using System;
using System.Globalization;
using System.Windows.Data;

namespace TaskScheduler.Converters
{
    /// <summary>TimeSpan &lt;-&gt; "HH:mm" 双向转换（仅取时分）</summary>
    public class TimeSpanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TimeSpan ts)
                return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}";
            return "00:00";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = (value as string)?.Trim();

            // 空输入 / 非法输入一律 DoNothing（保留原值）：
            // 旧实现非法输入静默变成 TimeSpan.Zero，任务会在毫无提示的情况下被改成 00:00 执行。
            if (string.IsNullOrEmpty(s)) return System.Windows.Data.Binding.DoNothing;

            return TimeText.TryParseTimeOfDay(s, out var ts)
                ? (object)ts
                : System.Windows.Data.Binding.DoNothing;
        }
    }
}
