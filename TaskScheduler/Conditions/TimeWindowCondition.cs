using System;
using System.Linq;

namespace TaskScheduler.Conditions
{
    /// <summary>
    /// 时段窗口条件：只在指定的时间段内允许执行。
    /// 用来给「每 30 分钟检查一次」这类高频任务加一道闸 —— 半夜别弹通知、别占用带宽，
    /// 出了窗口就静默跳过，到点自动恢复。
    ///
    /// 结束时刻小于开始时刻表示跨午夜（例如 22:00–06:00）。
    /// </summary>
    public class TimeWindowCondition : ConditionBase
    {
        public override ConditionType Type => ConditionType.TimeWindow;

        /// <summary>窗口开始时刻</summary>
        private TimeSpan _start = new TimeSpan(8, 0, 0);
        public TimeSpan Start
        {
            get => _start;
            set => SetField(ref _start, value);
        }

        /// <summary>窗口结束时刻；小于开始时刻表示跨午夜</summary>
        private TimeSpan _end = new TimeSpan(20, 0, 0);
        public TimeSpan End
        {
            get => _end;
            set => SetField(ref _end, value);
        }

        /// <summary>生效星期；为空表示每天。跨午夜时段按「窗口开始那一天」的星期判断。</summary>
        private DayOfWeek[] _days;
        public DayOfWeek[] Days
        {
            get => _days;
            set => SetField(ref _days, value);
        }

        protected override bool EvaluateCore() => IsInWindow(DateTime.Now);

        /// <summary>判定指定时刻是否落在窗口内（抽出来便于自测）</summary>
        public bool IsInWindow(DateTime now)
        {
            var t = now.TimeOfDay;
            bool cross = End < Start;

            bool inTime = cross
                ? (t >= Start || t <= End)
                : (t >= Start && t <= End);
            if (!inTime) return false;

            if (Days == null || Days.Length == 0) return true;

            // 跨午夜时凌晨那段（t <= End）属于"前一天开始"的窗口，星期要按前一天算，
            // 否则「周三 22:00–06:00」会把周四凌晨那段错判成周四。
            var day = (cross && t <= End) ? now.Date.AddDays(-1).DayOfWeek : now.DayOfWeek;
            return Array.Exists(Days, d => d == day);
        }

        protected override string DescribeCore
        {
            get
            {
                var txt = $"允许时段 {Fmt(Start)}–{Fmt(End)}";
                if (Days == null || Days.Length == 0) return txt + "（每天）";
                return txt + "（" + string.Join("/", Days.Select(EnumText.Day)) + "）";
            }
        }

        private static string Fmt(TimeSpan t) => $"{t.Hours:00}:{t.Minutes:00}";
    }
}
