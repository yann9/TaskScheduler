using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using TaskScheduler.Engine;

namespace TaskScheduler.Triggers
{
        /// <summary>
        /// 时间触发器：支持 一次性 / 每天 / 每周 / 每月 / 间隔 五种方式，
        /// 另可叠加随机抖动、跳过周末与法定节假日。
        /// 内部用单个 Timer 到点触发并自动重排下一次。
        /// </summary>
    public class TimeTrigger : ITrigger
    {
        public TriggerType Type => TriggerType.Time;

        public TimeTriggerKind Kind { get; set; } = TimeTriggerKind.Daily;

        // 一次性
        public DateTime? TargetTime { get; set; }

        // 每天
        public TimeSpan DailyTime { get; set; } = new TimeSpan(9, 0, 0);
        public int DaysInterval { get; set; } = 1;

        // 每周
        public DayOfWeek[] WeeklyDays { get; set; }
        public TimeSpan WeeklyTime { get; set; } = new TimeSpan(9, 0, 0);

        // 每月
        /// <summary>每月按哪种口径取日期</summary>
        public MonthlyMode MonthlyKind { get; set; } = MonthlyMode.DayOfMonth;

        /// <summary>每月第几天（1–31）。遇小月没有该日时自动落在当月最后一天，而不是跳过整月</summary>
        public int MonthlyDay { get; set; } = 1;

        /// <summary>第几个星期几（1–4；填 5 表示"当月最后一个"该星期几）</summary>
        public int MonthlyNth { get; set; } = 1;

        /// <summary>配合 MonthlyNth 使用的星期几</summary>
        public DayOfWeek MonthlyWeekday { get; set; } = DayOfWeek.Monday;

        /// <summary>每月的触发时刻</summary>
        public TimeSpan MonthlyTime { get; set; } = new TimeSpan(9, 0, 0);

        // 间隔
        public int IntervalValue { get; set; } = 30;
        public IntervalUnit IntervalUnit { get; set; } = IntervalUnit.Minutes;

        /// <summary>
        /// 间隔触发的"首次执行"策略：一个从未排期过的任务，第一次什么时候跑。
        /// 只影响"还没有下次执行时间"的任务（新建、或升级前的老存档）；
        /// 一旦有了下次执行时间，之后每次启动都沿用存档值，不再受这里影响。
        /// </summary>
        public IntervalFirstRunMode FirstRunMode { get; set; } = IntervalFirstRunMode.AfterInterval;

        /// <summary>"首次延迟"的数值（配合 FirstRunDelayUnit，仅 FirstRunMode = AfterDelay 时有效）</summary>
        public int FirstRunDelayValue { get; set; } = 1;

        /// <summary>"首次延迟"的单位（仅 FirstRunMode = AfterDelay 时有效）</summary>
        public IntervalUnit FirstRunDelayUnit { get; set; } = IntervalUnit.Minutes;

        /// <summary>
        /// 错过补偿：引擎启动（含开机 / 重启 / 引擎重启）时，若上一个计划时间点已经过去、
        /// 且那次从未执行过，就立即补执行一次。
        /// 默认关闭 —— cron、systemd timer、Windows 任务计划的默认行为都是"错过不补"，
        /// 避免开机瞬间集中补跑一堆任务；"每天备份"这类必须完成的活再单独勾上。
        /// </summary>
        public bool RunIfMissed { get; set; }

        /// <summary>
        /// 下次执行时间。会随任务一起持久化到 tasks.json：
        /// 引擎启动时若该时间还没到就直接沿用，不再重算。
        /// 这对"间隔"触发尤其关键 —— 旧实现每次启动都以"当前时刻 + 间隔"重排，
        /// 重启（或服务被保活拉起）会把已累计的进度清零；当重启周期短于间隔时任务永远等不到。
        /// </summary>
        public DateTime? NextRunTime { get; set; }

        /// <summary>
        /// 随机抖动（分钟，0 = 关闭）：真正触发的时刻在计划点之后随机推迟 0–N 分钟。
        ///
        /// 为什么需要：几十台机器都配了"每天 09:00 上报"，到点会同时打到同一台服务器上
        /// （惊群 / thundering herd）。加几分钟随机量就能把压力摊平。
        /// 另外也对"错峰访问被限流的接口"有用。
        ///
        /// 注意：抖动只影响实际唤醒时刻，NextRunTime 记录的仍是计划点，
        /// 所以界面上显示的时间不会随机跳；而且抖动值在一次触发内是固定的，
        /// 不会因为定时器重新武装而反复重抽（那会导致任务永远触发不了）。
        /// </summary>
        public int RandomJitterMinutes { get; set; }

        /// <summary>计划时间落在周六 / 周日时顺延到下一个工作日（调休补班的周末不算休息日）</summary>
        public bool SkipWeekend { get; set; }

        /// <summary>
        /// 计划时间落在法定节假日时顺延到下一个工作日。
        /// 依据 <see cref="ChineseCalendar"/>：优先用在线同步到的数据（本地缓存），
        /// 没同步到就退回内置表；都没覆盖的年份请用「额外跳过」手工补录。
        /// </summary>
        public bool SkipHolidays { get; set; }

        /// <summary>
        /// 额外需要跳过的日期，手工补录用。支持 yyyy-MM-dd，多个用逗号 / 分号 / 空格分隔。
        /// 主要用途：内置节假日表还没覆盖到的年份（国务院通常前一年 11 月才公布次年安排），
        /// 以及公司自己的年假、系统维护窗口。
        /// </summary>
        public string ExtraSkipDates { get; set; } = "";

        /// <summary>
        /// 是否曾经排过期（持久化）。用来区分两种"没有下次执行时间"：
        /// 从没排过（新建任务 → 按 FirstRunMode 决定首次）和
        /// 排过但被重置（改过计划 → 按新设置从当前时刻重排，不再走首次策略）。
        /// </summary>
        public bool HasScheduled { get; set; }

        /// <summary>
        /// 启动时若发现存档的计划点已经过去，记下"真正被错过的那一次"，供引擎按 RunIfMissed 决定是否补跑。
        /// 每次启动重新判定，不持久化。
        /// </summary>
        [JsonIgnore]
        public DateTime? MissedPlanPoint { get; private set; }

        /// <summary>下次执行时间发生变化时触发，供上层把排期写回存档（否则重启后会丢）。</summary>
        public event EventHandler ScheduleChanged;

        private System.Timers.Timer _timer;
        private readonly object _timerLock = new object();
        private volatile bool _stopped = true;

        public event EventHandler<TriggerFiredEventArgs> Fired;

        public void Start()
        {
            Stop();
            MissedPlanPoint = null;
            _stopped = false;

            var now = DateTime.Now;
            var saved = NextRunTime;

            if (saved.HasValue && saved.Value <= now)
            {
                // 存档的计划点已经过去（停机期间错过）→ 记下该时间点供补跑判断，并从当前时刻重排。
                MissedPlanPoint = saved.Value;
                NextRunTime = ComputeNext(now);
            }
            else if (!saved.HasValue)
            {
                // 没有下次执行时间：区分"从没排过期"（新建 → 按首次执行策略）
                // 与"排过但被重置"（改过计划 → 直接按新设置从当前时刻重排）。
                NextRunTime = HasScheduled ? ComputeNext(now) : ComputeFirstRun(now);
            }
            // else：存档的计划点还在未来 → 直接沿用，不重算（这是"重启不漂移"的关键）。

            HasScheduled = true;
            Schedule();
        }

        /// <summary>
        /// 停止并释放定时器。
        /// 必须与 Arm 互斥：Arm 正处在"换定时器引用"的中间态时被 Stop 插进来，
        /// 会把新定时器 Dispose 掉，随后 Arm 的 Start() 就在定时器线程上抛 ObjectDisposedException ——
        /// .NET Framework 下线程池回调的未捕获异常会直接终止进程。
        /// </summary>
        public void Stop()
        {
            _stopped = true;
            System.Timers.Timer t;
            lock (_timerLock) { t = _timer; _timer = null; }
            if (t == null) return;
            try { t.Stop(); t.Dispose(); } catch { }
        }

        private void Schedule()
        {
            if (NextRunTime == null) { RaiseScheduleChanged(); return; }

            // 计划点不动，只把"实际唤醒时刻"往后随推 0–N 分钟。
            var plan = NextRunTime.Value;
            var fireAt = plan;
            if (RandomJitterMinutes > 0 && plan > DateTime.Now)
                fireAt = plan.AddMilliseconds(NextJitterMilliseconds(RandomJitterMinutes));

            Arm(fireAt);
            RaiseScheduleChanged();
        }

        /// <summary>按指定的"真正到点时刻"武装定时器</summary>
        private void Arm(DateTime fireAt)
        {
            _armedFireAt = fireAt;

            var due = fireAt - DateTime.Now;
            // 下限取 1ms：System.Timers.Timer 不接受 <= 0 的间隔值（会抛 ArgumentException）。
            // "立即执行首次"时 due 会是 0 或负数，这里必须兜住。
            double ms = Math.Max(1, due.TotalMilliseconds);
            if (ms > int.MaxValue) ms = int.MaxValue;

            var timer = new System.Timers.Timer(ms) { AutoReset = false };
            timer.Elapsed += OnTimerElapsed;

            System.Timers.Timer old;
            lock (_timerLock)
            {
                // 先建新的、再换引用：这样 Stop 只可能拿到"新的那个"或"旧的空位"，
                // 不存在"新旧都不在 _timer 里、最终没人 Dispose"的泄漏。
                if (_stopped) { timer.Dispose(); return; }
                old = _timer;
                _timer = timer;
            }
            if (old != null) { try { old.Stop(); old.Dispose(); } catch { } }

            try { timer.Start(); }
            catch (ObjectDisposedException)
            {
                // 与并发 Stop 的竞争：对方已经把我们停掉并 Dispose 了，属于正常时序，忽略
            }
            catch (Exception ex) { Log.Error("武装定时器失败：" + ex.Message); }
        }

        private void OnTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_stopped) return;

            // 到这里可能其实还没到点，两种原因：
            // 1) 等待时间超过 System.Timers.Timer 的间隔上限（int.MaxValue 毫秒 ≈ 24.8 天）被截断；
            // 2) 随机抖动让定时器比计划点提前唤醒。
            // 两种都必须按**原来那个时刻**重新武装 —— 尤其是不能重新抽抖随机数，
            // 否则每轮都可能又被推迟，任务会来回漂移甚至永远等不到。
            if (DateTime.Now < _armedFireAt) { Arm(_armedFireAt); return; }

            Fired?.Invoke(this, new TriggerFiredEventArgs { Message = "定时触发" });
            if (Kind == TimeTriggerKind.OneTime) { Stop(); RaiseScheduleChanged(); return; }
            NextRunTime = ComputeNext(DateTime.Now);
            Schedule();
        }

        private DateTime _armedFireAt;

        private static readonly Random _jitterRandom = new Random();
        private static readonly object _jitterLock = new object();

        /// <summary>取 0–minutes 分钟之间的随机毫秒数（Random 不是线程安全的，加锁）</summary>
        private static double NextJitterMilliseconds(int minutes)
        {
            lock (_jitterLock) return _jitterRandom.NextDouble() * minutes * 60000.0;
        }

        private void RaiseScheduleChanged()
        {
            var h = ScheduleChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        /// <summary>
        /// "首次执行"时间：仅对还没有任何排期记录的任务生效。
        /// 非间隔方式没有这个概念，等同于正常的下一次计算。
        /// </summary>
        private DateTime? ComputeFirstRun(DateTime now)
        {
            if (Kind != TimeTriggerKind.Interval) return ComputeNext(now);

            switch (FirstRunMode)
            {
                case IntervalFirstRunMode.Immediately:
                    return now; // 立刻到点，Schedule 会以最小间隔立即触发
                case IntervalFirstRunMode.AfterDelay:
                    return now.Add(FirstRunDelay());
                default:
                    return ComputeNext(now); // 等一个完整间隔（旧行为）
            }
        }

        private TimeSpan FirstRunDelay()
        {
            int v = Math.Max(0, FirstRunDelayValue);
            return FirstRunDelayUnit == IntervalUnit.Hours
                ? TimeSpan.FromHours(v)
                : TimeSpan.FromMinutes(v);
        }

        public DateTime? ComputeNext(DateTime from)
        {
            switch (Kind)
            {
                case TimeTriggerKind.OneTime:
                    return ApplySkipRules(TargetTime != null && TargetTime > from ? TargetTime : (DateTime?)null);

                case TimeTriggerKind.Daily:
                {
                    var next = from.Date.Add(DailyTime);
                    while (next <= from) next = next.AddDays(Math.Max(1, DaysInterval));
                    return ApplySkipRules(next);
                }

                case TimeTriggerKind.Weekly:
                {
                    var days = WeeklyDays ?? new[] { from.DayOfWeek };
                    for (int i = 0; i < 14; i++)
                    {
                        var cand = from.Date.AddDays(i).Add(WeeklyTime);
                        if (cand > from && Array.Exists(days, d => d == cand.DayOfWeek))
                            return ApplySkipRules(cand);
                    }
                    return null;
                }

                case TimeTriggerKind.Monthly:
                    return ApplySkipRules(ComputeMonthly(from, true));

                case TimeTriggerKind.Interval:
                {
                    var step = IntervalUnit == IntervalUnit.Hours
                        ? TimeSpan.FromHours(IntervalValue)
                        : TimeSpan.FromMinutes(IntervalValue);
                    if (step <= TimeSpan.Zero) step = TimeSpan.FromMinutes(1);
                    // 间隔方式没有"哪一天"的概念，跳过周末 / 节假日对它不适用
                    return from.Add(step);
                }

                default:
                    return null;
            }
        }

        /// <summary>
        /// 排期签名：把所有影响"下次执行时间"的字段拼成一个字符串，用于比较两次排期是否相同。
        ///
        /// 为什么需要：编辑任务时不能无脑丢弃已存的 <see cref="NextRunTime"/>——
        /// 间隔任务的计划点是"上次锚点 + N 周期"，如果只改了动作/条件也重排，
        /// 锚点就会变成"编辑那一刻"，例如每 4 小时的任务在 13:38 改了个动作，
        /// 下次执行就从 14:46 漂移到 17:38，用户看起来就是"到点没跑"。
        /// 只有排期参数真的变了，才应该按新设置重新锚定。
        ///
        /// 注意：首次运行参数（FirstRunMode 等）**不在**这个签名里 —— 它们不改变周期本身，
        /// 混进来会让"只改首次参数"的任务被当成"排期大改"，锚点被无谓重置。
        /// 首次参数单独用 <see cref="FirstRunSignature"/> 比较。
        /// </summary>
        public string ScheduleSignature() => string.Join("|",
            (int)Kind,
            TargetTime?.ToString("o") ?? "",
            DailyTime, DaysInterval,
            WeeklyDays == null ? "" : string.Join(",", WeeklyDays.Select(d => (int)d).OrderBy(x => x)),
            WeeklyTime,
            (int)MonthlyKind, MonthlyDay, MonthlyNth, (int)MonthlyWeekday, MonthlyTime,
            IntervalValue, (int)IntervalUnit,
            RandomJitterMinutes, SkipWeekend, SkipHolidays, ExtraSkipDates.Trim());

        /// <summary>
        /// 首次运行参数签名（仅间隔方式有意义，其他方式恒为空串）。
        ///
        /// 单独拿出来比的原因：首次参数的生效路径和常规排期不同 —— 它要的是
        /// "按新策略重新算首次执行"（走 <see cref="ComputeFirstRun"/>），而不是
        /// 常规改排期的"从当前时刻重排"（走 <see cref="ComputeNext"/>）。
        /// 漏比的话，编辑器里改了首次参数会被判定成"排期没变"，完全不生效（踩过）。
        /// </summary>
        public string FirstRunSignature() => Kind == TimeTriggerKind.Interval
            ? string.Join("|", (int)FirstRunMode, FirstRunDelayValue, (int)FirstRunDelayUnit)
            : "";

        /// <summary>
        /// 把"跳过周末 / 跳过节假日 / 额外跳过日期"应用到候选时间点上：
        /// 命中就整体往后顺延一天（保持原来的时刻），直到落到一个该执行的日子。
        ///
        /// 顺延而不是直接丢掉这个计划点，是因为"每月 1 号的报表"遇到国庆应该改到 8 号出，
        /// 而不是整月不出。
        /// </summary>
        private DateTime? ApplySkipRules(DateTime? candidate)
        {
            if (!candidate.HasValue) return null;
            if (Kind == TimeTriggerKind.Interval) return candidate;

            var extra = ParseExtraSkipDates();
            if (!SkipWeekend && !SkipHolidays && extra.Count == 0) return candidate;

            var timeOfDay = candidate.Value.TimeOfDay;
            var d = candidate.Value;
            for (int i = 0; i < 60; i++)
            {
                var date = d.Date;
                if (!ChineseCalendar.ShouldSkip(date, SkipWeekend, SkipHolidays) && !extra.Contains(date))
                    return d;
                d = date.AddDays(1).Add(timeOfDay);
            }

            // 连续 60 天都命中（不可能出现，但绝不能在这里死循环）→ 放弃顺延，原样返回
            return candidate.Value;
        }

        /// <summary>解析「额外跳过日期」，支持 yyyy-MM-dd / yyyy/MM/dd / yyyyMMdd / MM-dd</summary>
        private HashSet<DateTime> ParseExtraSkipDates()
        {
            var set = new HashSet<DateTime>();
            var s = ExtraSkipDates;
            if (string.IsNullOrWhiteSpace(s)) return set;

            var formats = new[] { "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd", "yyyyMMdd", "MM-dd", "M-d" };
            foreach (var part in s.Split(new[] { ',', '，', ';', '；', ' ', '\r', '\n', '\t' },
                                         StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim();
                if (t.Length == 0) continue;

                if (DateTime.TryParseExact(t, formats, CultureInfo.InvariantCulture,
                                           DateTimeStyles.None, out var dt))
                {
                    // MM-dd 这种没写年份的按"今年的那一天"理解
                    if (t.Length <= 5 && dt.Year == DateTime.Now.Year)
                        dt = new DateTime(DateTime.Now.Year, dt.Month, dt.Day);
                    set.Add(dt.Date);
                }
                else if (DateTime.TryParse(t, CultureInfo.InvariantCulture,
                                           DateTimeStyles.None, out var dt2))
                {
                    set.Add(dt2.Date);
                }
            }
            return set;
        }

        /// <summary>
        /// 每月触发的时间点。向前、向后各最多扫 24 个月：
        /// 固定日遇小月会自动收敛到当月最后一天，所以每个月都必然有解，
        /// 扫 24 次只是兜底，正常一两次就命中。
        /// </summary>
        private DateTime? ComputeMonthly(DateTime from, bool forward)
        {
            var anchor = new DateTime(from.Year, from.Month, 1);
            for (int i = 0; i <= 24; i++)
            {
                var month = anchor.AddMonths(forward ? i : -i);
                var cand = MonthlyDateIn(month).Add(MonthlyTime);
                if (forward)
                {
                    if (cand > from) return cand;
                }
                else
                {
                    if (cand <= from) return cand;
                }
            }
            return null;
        }

        /// <summary>当月符合设置的那一天（0 点）</summary>
        private DateTime MonthlyDateIn(DateTime anyDayInMonth)
        {
            var first = new DateTime(anyDayInMonth.Year, anyDayInMonth.Month, 1);
            int daysInMonth = DateTime.DaysInMonth(first.Year, first.Month);

            switch (MonthlyKind)
            {
                case MonthlyMode.LastDay:
                    return first.AddDays(daysInMonth - 1);

                case MonthlyMode.NthWeekday:
                {
                    // nth = 5 表示"当月最后一个该星期几"：算出来会跨月，由下面的回退分支收敛
                    int nth = Math.Min(5, Math.Max(1, MonthlyNth));
                    int offset = ((int)MonthlyWeekday - (int)first.DayOfWeek + 7) % 7;
                    var d = first.AddDays(offset + (nth - 1) * 7);
                    return d.Month == first.Month
                        ? d
                        : LastWeekdayOf(first, daysInMonth);
                }

                default: // MonthlyMode.DayOfMonth
                {
                    // 31 号遇 2 月 / 4 月：落在当月最后一天，而不是跳过这一整月
                    int day = Math.Min(Math.Max(1, MonthlyDay), daysInMonth);
                    return first.AddDays(day - 1);
                }
            }
        }

        private DateTime LastWeekdayOf(DateTime first, int daysInMonth)
        {
            var last = first.AddDays(daysInMonth - 1);
            int back = ((int)last.DayOfWeek - (int)MonthlyWeekday + 7) % 7;
            return last.AddDays(-back);
        }

        /// <summary>
        /// 上一个"本该执行"的计划时间点，供错过补偿判断使用。
        /// 启动时若存档计划点已过，那个时间点才是真正被错过的那一次，优先用它
        /// （间隔触发就是靠它参与补跑的）；没有存档记录则按方式推算，
        /// 间隔方式没有固定计划点，返回 null。
        ///
        /// 注意这里必须先过一遍跳过规则：计划点若原本就落在周末/节假日而被顺延，
        /// 它就"本来不该执行" —— 顺延后仍在未来时绝不能补跑，否则勾了「跳过节假日」
        /// 反而在节假日当天补跑一次，跟开关的意图正好相反。
        /// </summary>
        public DateTime? ComputePrevious(DateTime now)
        {
            var prev = ComputePreviousRaw(now);
            if (!prev.HasValue) return null;

            var shifted = ApplySkipRules(prev);
            if (shifted.HasValue && shifted.Value > now) return null;
            return shifted ?? prev;
        }

        private DateTime? ComputePreviousRaw(DateTime now)
        {
            if (MissedPlanPoint.HasValue) return MissedPlanPoint;

            switch (Kind)
            {
                case TimeTriggerKind.OneTime:
                    return TargetTime;

                case TimeTriggerKind.Daily:
                {
                    int step = Math.Max(1, DaysInterval);
                    var prev = now.Date.Add(DailyTime);
                    if (prev > now) prev = prev.AddDays(-step);
                    return prev;
                }

                case TimeTriggerKind.Weekly:
                {
                    var days = WeeklyDays ?? new DayOfWeek[0];
                    for (int i = 0; i < 14; i++)
                    {
                        var cand = now.Date.AddDays(-i).Add(WeeklyTime);
                        if (cand <= now && Array.Exists(days, d => d == cand.DayOfWeek)) return cand;
                    }
                    return null;
                }

                case TimeTriggerKind.Monthly:
                    return ComputeMonthly(now, false);

                default:
                    return null;
            }
        }

        public string Describe()
        {
            var core = DescribeCore();
            var extra = new List<string>();

            if (RandomJitterMinutes > 0) extra.Add($"随机抖动 0–{RandomJitterMinutes} 分钟");

            // 间隔方式没有"哪一天"的概念，跳过规则对它无效，描述里也就不该出现
            if (Kind != TimeTriggerKind.Interval)
            {
                if (SkipWeekend) extra.Add("跳过周末");
                if (SkipHolidays) extra.Add("跳过法定节假日");
                var n = ParseExtraSkipDates().Count;
                if (n > 0) extra.Add($"额外跳过 {n} 天");
            }

            return extra.Count == 0 ? core : core + "，" + string.Join("、", extra);
        }

        private string DescribeCore()
        {
            switch (Kind)
            {
                case TimeTriggerKind.OneTime:
                    return $"一次性：{(TargetTime.HasValue ? TargetTime.Value.ToString("yyyy-MM-dd HH:mm") : "未设置")}";
                case TimeTriggerKind.Daily:
                    return $"每天 {DailyTime:hh\\:mm}（每 {Math.Max(1, DaysInterval)} 天）";
                case TimeTriggerKind.Weekly:
                {
                    var txt = WeeklyDays == null || WeeklyDays.Length == 0
                        ? "未选"
                        : string.Join("/", WeeklyDays.Select(d => d.ToString().Substring(0, 3)));
                    return $"每周 {txt} {WeeklyTime:hh\\:mm}";
                }
                case TimeTriggerKind.Monthly:
                {
                    string what;
                    switch (MonthlyKind)
                    {
                        case MonthlyMode.LastDay:
                            what = "最后一天";
                            break;
                        case MonthlyMode.NthWeekday:
                            what = MonthlyNth >= 5
                                ? "最后一个" + EnumText.Day(MonthlyWeekday)
                                : $"第 {Math.Min(5, Math.Max(1, MonthlyNth))} 个" + EnumText.Day(MonthlyWeekday);
                            break;
                        default:
                            what = Math.Max(1, MonthlyDay) + " 日";
                            break;
                    }
                    return $"每月 {what} {MonthlyTime:hh\\:mm}";
                }
                case TimeTriggerKind.Interval:
                {
                    var unit = IntervalUnit == IntervalUnit.Hours ? "小时" : "分钟";
                    var first = "";
                    if (FirstRunMode == IntervalFirstRunMode.Immediately)
                        first = "，首次立即执行";
                    else if (FirstRunMode == IntervalFirstRunMode.AfterDelay)
                        first = $"，首次延迟 {Math.Max(0, FirstRunDelayValue)} {(FirstRunDelayUnit == IntervalUnit.Hours ? "小时" : "分钟")}";
                    return $"每隔 {IntervalValue} {unit}{first}";
                }
                default:
                    return "时间触发";
            }
        }
    }
}
