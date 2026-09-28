using System;

namespace TaskScheduler
{
    /// <summary>
    /// 枚举的中文短名。
    /// 存在的意义：同一个"周五"原本要在下拉框转换器、触发器描述、条件描述里各写一遍 switch，
    /// 改一处漏两处；集中到这里之后只有一份。
    /// </summary>
    public static class EnumText
    {
        public static string Day(DayOfWeek d) => d switch
        {
            DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四",
            DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六",
            _ => "周日"
        };
    }
}
