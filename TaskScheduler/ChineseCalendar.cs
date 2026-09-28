using System;
using System.Collections.Generic;
using System.Linq;

namespace TaskScheduler
{
    /// <summary>某一年的节假日数据</summary>
    public sealed class CalendarYear
    {
        public int Year { get; set; }

        /// <summary>放假日（含调休拼出来的连休）</summary>
        public HashSet<DateTime> Holidays { get; set; } = new HashSet<DateTime>();

        /// <summary>调休补班日：这些天是周末但要上班</summary>
        public HashSet<DateTime> MakeupWorkdays { get; set; } = new HashSet<DateTime>();

        /// <summary>数据来源说明（"在线同步" / "本地缓存" / "内置"），用于界面展示</summary>
        public string Source { get; set; }

        /// <summary>国务院原文链接（只有外部数据源才会带）</summary>
        public List<string> Papers { get; set; } = new List<string>();
    }

    /// <summary>
    /// 中国法定节假日表（含调休补班日）。
    ///
    /// 数据分两层，外部优先、内置兜底：
    ///   1) 外部层 —— 从公开数据源（GitHub 上的 holiday-cn 项目，静态 JSON）
    ///      同步下来的年度安排，由 <see cref="Engine.HolidayProvider"/> 负责拉取与缓存。
    ///      每年 11 月国务院发布次年通知后，数据源会自动更新，程序不用改代码。
    ///   2) 内置层 —— 编译进程序的国务院通知数据，只覆盖 2025–2026。
    ///      网络不通、缓存也没落下来时用这份，保证离线下"跳过节假日"不会彻底失效。
    ///
    /// 为什么调休日必须单独记一份：
    /// 「跳过周末」如果无脑按 DayOfWeek 判断，就会把春节前那个周六（调休上班）也跳掉 ——
    /// 那天实际是工作日，该跑的任务就白白漏了。所以补班日的优先级高于周末。
    ///
    /// 无法解决的固有边界：任何数据源都不可能提前给出次年安排（通知没发就没有），
    /// 所以「今年 11 月之前用到明年」这个场景只能靠界面上手工补录「额外跳过日期」。
    /// </summary>
    public static class ChineseCalendar
    {
        /// <summary>内置层：随程序编译进来的数据，永远存在</summary>
        private static readonly Dictionary<int, CalendarYear> Builtin
            = new Dictionary<int, CalendarYear>();

        /// <summary>
        /// 外部层：同步 / 缓存下来的数据。
        /// 用「整体替换引用」而不是往里面逐个 Add —— 求值跑在多线程（引擎在后台线程判定条件与排期），
        /// 读侧拿到引用快照后无需加锁，也不会读到写了一半的字典。
        /// </summary>
        private static volatile Dictionary<int, CalendarYear> _external
            = new Dictionary<int, CalendarYear>();

        /// <summary>内置表覆盖的年份范围</summary>
        public static readonly int MinYear = 2025;
        public static readonly int MaxYear = 2026;

        static ChineseCalendar()
        {
            // ---- 2025 年（国办发 2024-11-12） ----
            var y2025 = new CalendarYear { Year = 2025, Source = "内置" };
            Range(y2025, 1, 1, 1, 1);      // 元旦 1/1
            Range(y2025, 1, 28, 2, 4);     // 春节 1/28–2/4
            Range(y2025, 4, 4, 4, 6);      // 清明
            Range(y2025, 5, 1, 5, 5);      // 劳动节
            Range(y2025, 5, 31, 6, 2);     // 端午
            Range(y2025, 10, 1, 10, 8);    // 国庆 + 中秋
            Makeup(y2025, (1, 26), (2, 8), (4, 27), (9, 28), (10, 11));   // 补班
            Builtin[2025] = y2025;

            // ---- 2026 年（国办发 2025-11-04） ----
            var y2026 = new CalendarYear { Year = 2026, Source = "内置" };
            Range(y2026, 1, 1, 1, 3);      // 元旦 1/1–1/3
            Range(y2026, 2, 15, 2, 23);    // 春节 2/15–2/23（9 天）
            Range(y2026, 4, 4, 4, 6);      // 清明
            Range(y2026, 5, 1, 5, 5);      // 劳动节
            Range(y2026, 6, 19, 6, 21);    // 端午
            Range(y2026, 9, 25, 9, 27);    // 中秋
            Range(y2026, 10, 1, 10, 7);    // 国庆
            Makeup(y2026, (1, 4), (2, 14), (2, 28), (5, 9), (9, 20), (10, 10));  // 补班
            Builtin[2026] = y2026;
        }

        private static void Range(CalendarYear y, int m1, int d1, int m2, int d2)
        {
            var from = new DateTime(y.Year, m1, d1);
            var to = new DateTime(y.Year, m2, d2);
            for (var d = from; d <= to; d = d.AddDays(1)) y.Holidays.Add(d);
        }

        private static void Makeup(CalendarYear y, params (int month, int day)[] items)
        {
            foreach (var it in items) y.MakeupWorkdays.Add(new DateTime(y.Year, it.month, it.day));
        }

        // ---------------------------------------------------------------- 外部数据

        /// <summary>
        /// 整体替换外部数据层。传 null 或空集合等于清空（回退到内置表）。
        /// 只接收「有数据」的年份 —— 数据源里 days 为空的年份（次年通知还没发布）
        /// 不应该覆盖任何东西，调用方负责过滤。
        /// </summary>
        public static void ApplyExternal(IEnumerable<CalendarYear> years)
        {
            var map = new Dictionary<int, CalendarYear>();
            if (years != null)
            {
                foreach (var y in years)
                {
                    if (y == null || y.Year <= 0) continue;
                    if (y.Holidays.Count == 0 && y.MakeupWorkdays.Count == 0) continue;
                    map[y.Year] = y;
                }
            }
            _external = map;   // 单次引用赋值，读侧天然一致
        }

        /// <summary>已加载的外部数据年份（升序）</summary>
        public static List<int> ExternalYears => _external.Keys.OrderBy(k => k).ToList();

        /// <summary>正在生效的年份（外部优先，其次内置），升序</summary>
        public static List<int> CoveredYears
            => Builtin.Keys.Union(_external.Keys).OrderBy(k => k).ToList();

        private static bool TryGetYear(int year, out CalendarYear data)
        {
            var ext = _external;   // 先取引用快照，避免读到替换了一半的字典
            if (ext != null && ext.TryGetValue(year, out data)) return true;
            return Builtin.TryGetValue(year, out data);
        }

        // ---------------------------------------------------------------- 查询

        /// <summary>是否处于放假期（不含补班日）</summary>
        public static bool IsHoliday(DateTime date)
            => TryGetYear(date.Year, out var y) && y.Holidays.Contains(date.Date);

        /// <summary>是否调休补班日（周末但需要上班）</summary>
        public static bool IsMakeupWorkday(DateTime date)
            => TryGetYear(date.Year, out var y) && y.MakeupWorkdays.Contains(date.Date);

        /// <summary>是否为周末</summary>
        public static bool IsWeekend(DateTime date)
            => date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;

        /// <summary>是否有该年份的数据（外部或内置任一有即可）</summary>
        public static bool IsCoveredYear(int year) => TryGetYear(year, out _);

        /// <summary>某年数据的来源说明；没有数据返回 null</summary>
        public static string SourceOf(int year)
            => TryGetYear(year, out var y) ? y.Source : null;

        /// <summary>界面上说明数据覆盖范围的文案（随同步状态变化）</summary>
        public static string CoverageText
        {
            get
            {
                var ext = ExternalYears;
                var builtinRange = $"内置兜底 {MinYear}–{MaxYear}";
                if (ext.Count == 0) return $"尚未同步到在线数据，{builtinRange}";
                return $"{string.Join("、", ext)} 年已同步，{builtinRange}";
            }
        }

        /// <summary>
        /// 综合判断某天是否应当跳过执行。
        /// 顺序很关键：补班日先判 —— 调休上班的那个周末要照常跑，不能被周末规则跳掉。
        /// </summary>
        public static bool ShouldSkip(DateTime date, bool skipWeekend, bool skipHolidays)
        {
            var d = date.Date;
            if (IsMakeupWorkday(d)) return false;
            if (skipHolidays && IsHoliday(d)) return true;
            if (skipWeekend && IsWeekend(d)) return true;
            return false;
        }
    }
}
