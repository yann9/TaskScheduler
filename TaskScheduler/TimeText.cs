using System;
using System.Globalization;

namespace TaskScheduler
{
    /// <summary>界面时间输入解析</summary>
    internal static class TimeText
    {
        /// <summary>
        /// 严格解析"时:分[:秒]"。
        ///
        /// 不能用 TimeSpan.TryParse：它把 "8" 当成 **8 天**（TimeSpan 的单位规则），
        /// 想让任务 8 点执行的人输入 "8" 就会得到一个"每 8 天执行一次"的任务。
        /// 非法输入也不再悄悄退化成 TimeSpan.Zero（那会把任务改成每天 00:00 执行）。
        /// 支持全角逗号 / 点号分隔。
        /// </summary>
        public static bool TryParseTimeOfDay(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var s = text.Trim()
                        .Replace('：', ':')   // 中文冒号
                        .Replace('.', ':')
                        .Replace('。', ':');

            var parts = s.Split(':');
            if (parts.Length < 2 || parts.Length > 3) return false;

            if (!int.TryParse(parts[0].Trim(), out var h) || h < 0 || h > 23) return false;
            if (!int.TryParse(parts[1].Trim(), out var m) || m < 0 || m > 59) return false;

            int sec = 0;
            if (parts.Length == 3)
            {
                if (!int.TryParse(parts[2].Trim(), out sec) || sec < 0 || sec > 59) return false;
            }

            value = new TimeSpan(h, m, sec);
            return true;
        }

        /// <summary>解析日期时间（用于"一次性"任务的执行时刻），先按常见格式精确匹配，再走系统解析</summary>
        public static bool TryParseDateTime(string text, out DateTime value)
        {
            value = default(DateTime);
            if (string.IsNullOrWhiteSpace(text)) return false;
            var s = text.Trim();

            var formats = new[]
            {
                "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss",
                "yyyy/MM/dd HH:mm", "yyyy/MM/dd HH:mm:ss",
                "yyyy-M-d H:m", "yyyy/M/d H:m"
            };
            if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out value))
                return true;

            return DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out value);
        }
    }
}
