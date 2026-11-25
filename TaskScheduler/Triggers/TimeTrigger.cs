using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using TaskScheduler.Engine;

namespace TaskScheduler.Triggers
{
        /// <summary>
        /// 时间触发器。界面上只暴露三类「计划规则」：
        ///   1) 日历排期（Kind = Calendar）：日期规则 × 执行时刻列表；
        ///   2) 时段内执行（Kind = Window）：起止时段 × 随机一点 / 固定间隔；
        ///   3) 连续间隔（Kind = Interval）：每 N 秒 / 分 / 时。
        /// 旧版八种方式（OneTime / Daily / Weekly / Monthly / MultiTime / RandomWindow / WindowInterval）
        /// 的存档在反序列化时由 <see cref="MigrateLegacyKind"/> 无损映射到这三类，UI 不再展示。
        /// 另可叠加随机抖动、跳过周末与法定节假日；内部用单个 Timer 到点触发并自动重排。
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

        // 时间段内随机
        /// <summary>
        /// 随机时段的起始时刻（默认 09:00）。旧存档里没有该字段，反序列化后取默认值；
        /// 由于旧任务的 Kind 不可能是 RandomWindow，这些默认值不会影响任何已有触发方式的行为。
        /// </summary>
        public TimeSpan RandomWindowStart { get; set; } = new TimeSpan(9, 0, 0);

        /// <summary>
        /// 随机时段的结束时刻（默认 10:00）。小于等于起始时刻表示窗口跨午夜延续到次日；
        /// 与起始时刻相等时按整整一天（24 小时）的窗口处理。
        /// </summary>
        public TimeSpan RandomWindowEnd { get; set; } = new TimeSpan(10, 0, 0);

        /// <summary>每隔多少天挑一个窗口执行（默认 1 = 每天）；小于 1 时按 1 处理</summary>
        public int RandomWindowDaysInterval { get; set; } = 1;

        /// <summary>
        /// 「每隔 N 天」的内部锚点：本次随机窗口所在的日期（0 点），用于让执行日序列
        /// 按绝对日历对齐、不随重启 / 重开程序漂移。首次排期取当前日期写入，
        /// 之后每次排期写回本次选中的窗口日。
        /// 注意：这不是用户配置项，也不进排期签名（改它不应触发重排），但必须随任务持久化，
        /// 否则重启后锚点丢失、周期会从重启当天重新起算。
        /// </summary>
        public DateTime? RandomWindowAnchorDate { get; set; }

        // 指定多个时刻
        /// <summary>
        /// 一天内要执行的多个时刻，格式 "08:30,12:00:15,18:00"，支持到秒。
        /// 解析时自动去重、按时间先后排序；非法项直接忽略。留空视为未配置（ComputeNext 返回 null）。
        /// 用字符串而不是 List&lt;TimeSpan&gt;：编辑器里就是一个输入框，存档与 IPC 也不用额外白名单。
        /// </summary>
        public string MultiTimesText { get; set; } = "09:00";

        /// <summary>「指定多个时刻」每隔多少天执行一次（默认 1 = 每天）；小于 1 按 1 处理</summary>
        public int MultiTimesDaysInterval { get; set; } = 1;

        /// <summary>「指定多个时刻」的日历锚点，语义同 RandomWindowAnchorDate，不进排期签名</summary>
        public DateTime? MultiTimesAnchorDate { get; set; }

        // 时间段内间隔（原生排期，不依赖「允许时段」条件）
        /// <summary>间隔窗口起始时刻，默认 09:00</summary>
        public TimeSpan WindowIntervalStart { get; set; } = new TimeSpan(9, 0, 0);

        /// <summary>间隔窗口结束时刻，默认 18:00；小于等于起始时刻表示跨午夜（相等为 24 小时）</summary>
        public TimeSpan WindowIntervalEnd { get; set; } = new TimeSpan(18, 0, 0);

        /// <summary>窗口内相邻两次执行的间隔数值；下限 1 秒</summary>
        public int WindowIntervalValue { get; set; } = 30;

        /// <summary>窗口内间隔单位（秒 / 分 / 时）</summary>
        public IntervalUnit WindowIntervalUnit { get; set; } = IntervalUnit.Minutes;

        /// <summary>「时间段内间隔」每隔多少天执行一次（默认 1 = 每天）</summary>
        public int WindowIntervalDaysInterval { get; set; } = 1;

        /// <summary>「时间段内间隔」的日历锚点，语义同 RandomWindowAnchorDate，不进排期签名</summary>
        public DateTime? WindowIntervalAnchorDate { get; set; }

        // ===== 统一模型：三类计划规则（旧八种在反序列化时迁入这些字段）=====

        // 日历排期 Calendar —— 日期规则 × 执行时刻列表
        /// <summary>日历排期的日期规则（旧 一次性 / 每天 / 每周 / 每月 / 多时刻 迁入此处）</summary>
        public CalendarDayMode CalendarDayMode { get; set; } = CalendarDayMode.EveryNDays;

        /// <summary>
        /// 日历排期的执行时刻列表，"08:30,12:00:15,18:00"，精确到秒；
        /// 统一替代旧的 DailyTime / WeeklyTime / MonthlyTime / MultiTimesText / OneTime 的时刻部分。
        /// </summary>
        public string CalendarTimes { get; set; } = "09:00";

        /// <summary>「指定日期」规则下的日期列表，格式 yyyy-MM-dd，多个用逗号 / 分号 / 空格分隔</summary>
        public string CalendarDates { get; set; } = "";

        /// <summary>「每隔 N 天」规则：每隔多少天（复用旧 DaysInterval 的语义，独立成字段避免旧存档串味）</summary>
        public int CalendarDaysInterval { get; set; } = 1;

        /// <summary>「每周」规则：勾选的星期几（复用旧 WeeklyDays 存储结构）</summary>
        public DayOfWeek[] CalendarWeekdays { get; set; }

        /// <summary>「每月」规则的口径（复用旧 MonthlyMode）</summary>
        public MonthlyMode CalendarMonthlyMode { get; set; } = MonthlyMode.DayOfMonth;

        /// <summary>「每月」固定日 / 第 N 个星期几的序号</summary>
        public int CalendarMonthlyValue { get; set; } = 1;

        /// <summary>「每月」第 N 个星期几中的星期几</summary>
        public DayOfWeek CalendarMonthlyWeekday { get; set; } = DayOfWeek.Monday;

        /// <summary>「每隔 N 天」的日历锚点：内部状态，不进排期签名，随任务持久化防漂移</summary>
        public DateTime? CalendarAnchorDate { get; set; }

        // 时段内执行 Window —— 起止时段 × 取值方式
        /// <summary>时段内执行的取值方式（旧 RandomWindow / WindowInterval 迁入此处）</summary>
        public WindowMode WindowMode { get; set; } = WindowMode.Interval;

        /// <summary>时段内执行的起始时刻</summary>
        public TimeSpan WindowStart { get; set; } = new TimeSpan(9, 0, 0);

        /// <summary>时段内执行的结束时刻；小于等于起始时刻表示跨午夜</summary>
        public TimeSpan WindowEnd { get; set; } = new TimeSpan(18, 0, 0);

        /// <summary>时段内执行的步长数值（Random 模式下忽略）</summary>
        public int WindowStepValue { get; set; } = 30;

        /// <summary>时段内执行的步长单位（Random 模式下忽略）</summary>
        public IntervalUnit WindowStepUnit { get; set; } = IntervalUnit.Minutes;

        /// <summary>「时段内执行」每隔多少天一次</summary>
        public int WindowDaysInterval { get; set; } = 1;

        /// <summary>「时段内执行」的日历锚点：内部状态，不进排期签名</summary>
        public DateTime? WindowAnchorDate { get; set; }

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

        // ===== 旧八种方式 → 三类统一规则的迁移 =====

        /// <summary>
        /// Newtonsoft 反序列化完成后自动调用（本地存档 / 命名管道 / 导入文件都走同一条反序列化路径，
        /// 所以一处迁移即可覆盖全部入口）。
        ///
        /// 原则：只在旧 Kind（0–7）上迁移，迁移完把 Kind 改成新值；已是新值则原样不动，
        /// 保证幂等（Clone() 会反复序列化 / 反序列化，必须重复调用不炸）。
        /// 排期相关状态（NextRunTime / HasScheduled / 各锚点）一律保留 —— 老任务的"下次执行时间"
        /// 已经被引擎持久化，换成等价规则后必须接着用，否则升级后所有任务会重新起算。
        /// </summary>
        [System.Runtime.Serialization.OnDeserialized]
        private void OnDeserialized(System.Runtime.Serialization.StreamingContext context)
        {
            MigrateLegacyKind();
        }

        /// <summary>把旧八种触发方式的字段无损映射到三类统一规则</summary>
        internal void MigrateLegacyKind()
        {
            switch (Kind)
            {
                case TimeTriggerKind.OneTime:
                    CalendarDayMode = CalendarDayMode.SpecificDates;
                    CalendarTimes = TargetTime.HasValue
                        ? TargetTime.Value.ToString("HH:mm:ss") : "09:00";
                    CalendarDates = TargetTime.HasValue
                        ? TargetTime.Value.ToString("yyyy-MM-dd") : "";
                    Kind = TimeTriggerKind.Calendar;
                    break;

                case TimeTriggerKind.Daily:
                    CalendarDayMode = CalendarDayMode.EveryNDays;
                    CalendarDaysInterval = DaysInterval;
                    CalendarTimes = DailyTime.ToString(@"hh\:mm\:ss");
                    CalendarAnchorDate = null; // 旧"每天"按 from.Date 逐日算，没有绝对锚点
                    Kind = TimeTriggerKind.Calendar;
                    break;

                case TimeTriggerKind.Weekly:
                    CalendarDayMode = CalendarDayMode.Weekly;
                    CalendarWeekdays = WeeklyDays;
                    CalendarTimes = WeeklyTime.ToString(@"hh\:mm\:ss");
                    Kind = TimeTriggerKind.Calendar;
                    break;

                case TimeTriggerKind.Monthly:
                    CalendarDayMode = CalendarDayMode.Monthly;
                    CalendarMonthlyMode = MonthlyKind;
                    CalendarMonthlyValue = MonthlyKind == MonthlyMode.NthWeekday ? MonthlyNth : MonthlyDay;
                    CalendarMonthlyWeekday = MonthlyWeekday;
                    CalendarTimes = MonthlyTime.ToString(@"hh\:mm\:ss");
                    Kind = TimeTriggerKind.Calendar;
                    break;

                case TimeTriggerKind.MultiTime:
                    CalendarDayMode = CalendarDayMode.EveryNDays;
                    CalendarDaysInterval = MultiTimesDaysInterval;
                    CalendarTimes = MultiTimesText;
                    CalendarAnchorDate = MultiTimesAnchorDate;
                    Kind = TimeTriggerKind.Calendar;
                    break;

                case TimeTriggerKind.RandomWindow:
                    WindowMode = WindowMode.Random;
                    WindowStart = RandomWindowStart;
                    WindowEnd = RandomWindowEnd;
                    WindowDaysInterval = RandomWindowDaysInterval;
                    WindowAnchorDate = RandomWindowAnchorDate;
                    Kind = TimeTriggerKind.Window;
                    break;

                case TimeTriggerKind.WindowInterval:
                    WindowMode = WindowMode.Interval;
                    WindowStart = WindowIntervalStart;
                    WindowEnd = WindowIntervalEnd;
                    WindowStepValue = WindowIntervalValue;
                    WindowStepUnit = WindowIntervalUnit;
                    WindowDaysInterval = WindowIntervalDaysInterval;
                    WindowAnchorDate = WindowIntervalAnchorDate;
                    Kind = TimeTriggerKind.Window;
                    break;

                // Interval(3) 本身就是统一后的"连续间隔"，Calendar(8) / Window(9) 已是新值：无需迁移
            }
        }

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
            // 「时间段内随机」本身即随机点；「指定多个时刻」是用户指定的精确时刻；
            // 「时间段内间隔」的点是窗口内按步长排出来的。三者叠加抖动都会破坏
            // 用户设定的时刻语义（尤其会把唤醒推到窗口之外），所以统一跳过抖动。
            var plan = NextRunTime.Value;
            var fireAt = plan;
            if (!SuppressJitter && RandomJitterMinutes > 0 && plan > DateTime.Now)
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
            // 原「一次性」迁移为 Calendar + SpecificDates：两者的日期都是有限列表，
            // 排完最后一个点后 ComputeNext 会返回 null，Schedule() 自然不再武装定时器。
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
                : FirstRunDelayUnit == IntervalUnit.Seconds
                    ? TimeSpan.FromSeconds(v)
                    : TimeSpan.FromMinutes(v);
        }

        /// <summary>
        /// 本身就是"指定时刻"或"窗口内定点"的方式不叠加随机抖动（见 Schedule）。
        /// 统一后 Calendar（时刻是自己指定的精确点）与 Window（点在窗口内排列）都跳过；
        /// 只有连续间隔 Interval 才叠加抖动。
        /// </summary>
        private bool SuppressJitter => Kind == TimeTriggerKind.Calendar
                                     || Kind == TimeTriggerKind.Window;

        public DateTime? ComputeNext(DateTime from)
        {
            switch (Kind)
            {
                case TimeTriggerKind.Calendar:
                    return ComputeCalendarNext(from);

                case TimeTriggerKind.Window:
                    return ComputeWindowNext(from);

                case TimeTriggerKind.Interval:
                    // 下限 1 秒（选"秒"时不再被旧的 1 分钟下限卡住）
                    return from.Add(Step(IntervalValue, IntervalUnit, TimeSpan.FromSeconds(1)));

                default:
                    // 理论上不该出现：旧八种在反序列化时已迁移。防御性返回 null 而不是抛异常，
                    // 避免一份手工构造、没走反序列化的存档让整个引擎崩掉。
                    return null;
            }
        }

        /// <summary>
        /// 「日历排期」的下一次执行时间 = 日期规则选出的某一天 × 时刻列表里第一个晚于 from 的时刻。
        /// 四个日期规则各自负责推进日历（含锚点写回），时刻列表统一解析一次。
        /// </summary>
        private DateTime? ComputeCalendarNext(DateTime from)
        {
            var times = ParseCalendarTimes();
            if (times.Count == 0) return null;

            switch (CalendarDayMode)
            {
                case CalendarDayMode.SpecificDates:
                {
                    // 指定日期 + 指定时刻：遍历所有 (日期 × 时刻) 组合，取严格晚于 from 的第一个。
                    // 日期是无序输入，先排序；不过最后再整体排序更稳妥（避免时刻跨组合乱序）。
                    var candidates = new List<DateTime>();
                    foreach (var d in ParseCalendarDates())
                        foreach (var t in times)
                            candidates.Add(d.Add(t));
                    candidates.Sort();
                    foreach (var c in candidates)
                        if (c > from) return ApplySkipRules(c);
                    return null;
                }

                case CalendarDayMode.EveryNDays:
                {
                    if (!CalendarAnchorDate.HasValue) CalendarAnchorDate = from.Date;
                    var anchor = CalendarAnchorDate.Value.Date;
                    int step = Math.Max(1, CalendarDaysInterval);
                    for (int i = 0; i < 400; i++)
                    {
                        var day = from.Date.AddDays(i);
                        int diff = (day - anchor).Days;
                        if (diff < 0 || diff % step != 0) continue;

                        foreach (var t in times)
                        {
                            var cand = day.Add(t);
                            if (cand <= from) continue;
                            CalendarAnchorDate = day;
                            return ApplySkipRules(cand);
                        }
                    }
                    return null;
                }

                case CalendarDayMode.Weekly:
                {
                    var days = CalendarWeekdays ?? new DayOfWeek[0];
                    for (int i = 0; i < 14; i++)
                    {
                        var day = from.Date.AddDays(i);
                        if (!Array.Exists(days, d => d == day.DayOfWeek)) continue;
                        foreach (var t in times)
                        {
                            var cand = day.Add(t);
                            if (cand <= from) continue;
                            return ApplySkipRules(cand);
                        }
                    }
                    return null;
                }

                case CalendarDayMode.Monthly:
                {
                    for (int i = 0; i <= 24; i++)
                    {
                        var month = new DateTime(from.Year, from.Month, 1).AddMonths(i);
                        var date = CalendarMonthlyDateIn(month);
                        foreach (var t in times)
                        {
                            var cand = date.Add(t);
                            if (cand <= from) continue;
                            return ApplySkipRules(cand);
                        }
                    }
                    return null;
                }

                default:
                    return null;
            }
        }

        /// <summary>解析「日历排期」的时刻列表：逗号 / 分号 / 空格分隔，去重升序，非法项忽略</summary>
        private List<TimeSpan> ParseCalendarTimes()
        {
            var list = new List<TimeSpan>();
            var s = CalendarTimes ?? "";
            foreach (var part in s.Split(new[] { ',', '，', ';', '；', ' ', '\r', '\n', '\t' },
                                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (TimeText.TryParseTimeOfDay(part.Trim(), out var ts) && !list.Contains(ts))
                    list.Add(ts);
            }
            list.Sort();
            return list;
        }

        /// <summary>解析「指定日期」列表：支持 yyyy-MM-dd / yyyy/MM/dd / yyyyMMdd，去重升序</summary>
        private List<DateTime> ParseCalendarDates()
        {
            var list = new List<DateTime>();
            var s = CalendarDates ?? "";
            var formats = new[] { "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd", "yyyyMMdd" };
            foreach (var part in s.Split(new[] { ',', '，', ';', '；', ' ', '\r', '\n', '\t' },
                                         StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim();
                if (DateTime.TryParseExact(t, formats, CultureInfo.InvariantCulture,
                                           DateTimeStyles.None, out var dt) && !list.Contains(dt.Date))
                    list.Add(dt.Date);
            }
            list.Sort();
            return list;
        }

        /// <summary>「每月」规则：给定月份中符合设置的那一天（0 点）</summary>
        private DateTime CalendarMonthlyDateIn(DateTime anyDayInMonth)
        {
            var first = new DateTime(anyDayInMonth.Year, anyDayInMonth.Month, 1);
            int daysInMonth = DateTime.DaysInMonth(first.Year, first.Month);

            switch (CalendarMonthlyMode)
            {
                case MonthlyMode.LastDay:
                    return first.AddDays(daysInMonth - 1);

                case MonthlyMode.NthWeekday:
                {
                    int nth = Math.Min(5, Math.Max(1, CalendarMonthlyValue));
                    int offset = ((int)CalendarMonthlyWeekday - (int)first.DayOfWeek + 7) % 7;
                    var d = first.AddDays(offset + (nth - 1) * 7);
                    return d.Month == first.Month
                        ? d
                        : CalendarLastWeekdayOf(first, daysInMonth);
                }

                default: // DayOfMonth
                {
                    int day = Math.Min(Math.Max(1, CalendarMonthlyValue), daysInMonth);
                    return first.AddDays(day - 1);
                }
            }
        }

        private DateTime CalendarLastWeekdayOf(DateTime first, int daysInMonth)
        {
            var last = first.AddDays(daysInMonth - 1);
            int back = ((int)last.DayOfWeek - (int)CalendarMonthlyWeekday + 7) % 7;
            return last.AddDays(-back);
        }

        /// <summary>
        /// 「时段内执行」的下一次执行时间：时段起点 × 步长逐点（Interval）或窗口内随机一点（Random）。
        /// 排期本身只落在窗口内，窗口外不会产生触发。步长可到秒，窗口支持跨午夜。
        /// </summary>
        private DateTime? ComputeWindowNext(DateTime from)
        {
            var start = NormalizeToDay(WindowStart);
            var end = NormalizeToDay(WindowEnd);
            var length = end > start ? end - start : TimeSpan.FromHours(24) - (start - end);
            if (length <= TimeSpan.Zero) length = TimeSpan.FromHours(24);

            if (!WindowAnchorDate.HasValue) WindowAnchorDate = from.Date;
            var anchor = WindowAnchorDate.Value.Date;
            int daysStep = Math.Max(1, WindowDaysInterval);

            for (int i = 0; i < 400; i++)
            {
                var day = from.Date.AddDays(-1 + i);
                int diff = (day - anchor).Days;
                if (diff < 0 || diff % daysStep != 0) continue;

                var wStart = day.Add(start);
                var wEnd = wStart.Add(length);
                if (wEnd <= from) continue;

                DateTime cand;
                if (WindowMode == WindowMode.Random)
                {
                    // 随机点必须严格晚于 from；窗口起点已过则重新往后续一天，避免当天重复抽点
                    if (wStart <= from) continue;
                    double offsetMs;
                    lock (_jitterLock)
                        offsetMs = _jitterRandom.NextDouble() * length.TotalMilliseconds;
                    cand = wStart.AddMilliseconds(offsetMs);
                }
                else
                {
                    var step = Step(WindowStepValue, WindowStepUnit, TimeSpan.FromSeconds(1));
                    if (step >= length) step = length;
                    if (wStart > from)
                    {
                        cand = wStart;
                    }
                    else
                    {
                        long elapsed = (from - wStart).Ticks;
                        long k = elapsed / step.Ticks;
                        if (wStart.AddTicks(k * step.Ticks) <= from) k++;
                        cand = wStart.AddTicks(k * step.Ticks);
                    }
                    if (cand >= wEnd) continue;
                }

                WindowAnchorDate = day;
                return ApplySkipRules(cand);
            }
            return null;
        }

        /// <summary>
        /// 「时间段内随机」的下一次执行时间。
        ///
        /// 关键语义：算出来的随机点本身就是 <see cref="NextRunTime"/>（不是"计划点 + 抖动"），
        /// 所以界面显示的"下次执行时间"就是本次真实执行时刻；该值随任务一起存档，
        /// 重启 / 重开程序时 <see cref="Start"/> 的"存档计划点还在未来就沿用"分支会直接沿用，
        /// 不会重新抽点（否则每次开机执行时间都会变）。
        /// 触发后 <see cref="OnTimerElapsed"/> 以 DateTime.Now 再次调用本方法，
        /// 因为要求窗口起点严格晚于当前时刻，不会在同一天再次选中同一个窗口，也就不会重复执行。
        /// </summary>
        private DateTime? ComputeRandomWindowNext(DateTime from)
        {
            // 起止时刻归一化到 [0, 24h)：用户可能填了 25:00、负数或跨多天的值，
            // 直接加到日期上会超出 DateTime 范围抛异常。TimeSpan 没有 TimeOfDay 属性，
            // 这里用"按一天的 ticks 取模、负数回绕"实现同样的归一化效果。
            var start = NormalizeToDay(RandomWindowStart);
            var end = NormalizeToDay(RandomWindowEnd);

            // 窗口长度：end > start 是当天内的一段；end <= start 表示跨午夜延续到次日
            // （end == start 视为整整 24 小时）。长度异常时兜底为 1 分钟，避免随机区间退化。
            var length = end > start
                ? end - start
                : TimeSpan.FromHours(24) - (start - end);
            if (length <= TimeSpan.Zero) length = TimeSpan.FromMinutes(1);

            // 锚点：首次取当前日期，之后沿用存档值，让"每隔 N 天"按绝对日历对齐、不随重启漂移。
            if (!RandomWindowAnchorDate.HasValue) RandomWindowAnchorDate = from.Date;
            var anchor = RandomWindowAnchorDate.Value.Date;
            int step = Math.Max(1, RandomWindowDaysInterval);

            // 从前一天开始逐日扫描（最多 400 天兜底）：跨午夜窗口的随机点可能落在次日凌晨，
            // 此时 from.Date 已经变成次日，从 -1 起能覆盖到"窗口起始日仍比 from.Date 早一天"的边界；
            // 而 wStart 严格晚于 from，保证选中当天晚上尚未到达的窗口、不会整跳一天。
            var scanStart = from.Date.AddDays(-1);
            for (int i = 0; i < 400; i++)
            {
                var day = scanStart.AddDays(i);
                int diff = (day - anchor).Days;
                if (diff < 0 || diff % step != 0) continue;

                var wStart = day.Add(start);
                if (wStart <= from) continue;

                // 在 [wStart, wStart + length) 内均匀取一个随机毫秒偏移。
                // 用 double 计算而非 int：窗口最长 24h = 86400000ms 虽小于 int.MaxValue，
                // 但 double 更稳妥（长度理论上更大时也不会溢出）。
                // 复用全局 Random 并加锁 —— Random 不是线程安全的。
                double offsetMs = 0;
                lock (_jitterLock)
                {
                    offsetMs = _jitterRandom.NextDouble() * length.TotalMilliseconds;
                }
                var point = wStart.AddMilliseconds(offsetMs);

                // 写回本次窗口日，供下一次按同一日历节奏对齐（不进排期签名）。
                RandomWindowAnchorDate = day;

                // 过跳过规则：命中周末 / 节假日时保持时刻顺延，仍落在同一时间段口径内，语义合理。
                return ApplySkipRules(point);
            }

            return null;
        }

        /// <summary>
        /// 「指定多个时刻」的下一次执行时间。
        /// 列出几个时刻就每天各跑几次；「每隔 N 天」按锚点日历对齐，不随重启漂移。
        /// 每次算出的都是确定的时刻（不是随机点），并随任务存档，重启后沿用。
        /// </summary>
        private DateTime? ComputeMultiTimeNext(DateTime from)
        {
            var times = ParseMultiTimes();
            if (times.Count == 0) return null;

            if (!MultiTimesAnchorDate.HasValue) MultiTimesAnchorDate = from.Date;
            var anchor = MultiTimesAnchorDate.Value.Date;
            int step = Math.Max(1, MultiTimesDaysInterval);

            // 最多扫 400 天兜底：正常情况下当天或下个对齐日就能命中
            for (int i = 0; i < 400; i++)
            {
                var day = from.Date.AddDays(i);
                int diff = (day - anchor).Days;
                if (diff < 0 || diff % step != 0) continue;

                foreach (var t in times)
                {
                    var cand = day.Add(t);
                    if (cand <= from) continue;
                    MultiTimesAnchorDate = day;
                    return ApplySkipRules(cand);
                }
            }
            return null;
        }

        /// <summary>解析「指定多个时刻」输入框：逗号 / 分号 / 空格 / 换行分隔，去重并升序排列，非法项忽略</summary>
        private List<TimeSpan> ParseMultiTimes()
        {
            var list = new List<TimeSpan>();
            var s = MultiTimesText ?? "";
            foreach (var part in s.Split(new[] { ',', '，', ';', '；', ' ', '\r', '\n', '\t' },
                                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (TimeText.TryParseTimeOfDay(part.Trim(), out var ts) && !list.Contains(ts))
                    list.Add(ts);
            }
            list.Sort();
            return list;
        }

        /// <summary>
        /// 「时间段内间隔」的下一次执行时间。
        ///
        /// 排期本身只落在窗口内：从窗口起点按固定步长逐点排列，取严格晚于 from 的第一个点。
        /// 因此不需要再挂「允许时段」条件 —— 窗口外根本不会生成计划点，也不会产生
        /// "触发了又被条件跳过"的空运行记录。步长可到秒，窗口支持跨午夜。
        /// </summary>
        private DateTime? ComputeWindowIntervalNext(DateTime from)
        {
            var start = NormalizeToDay(WindowIntervalStart);
            var end = NormalizeToDay(WindowIntervalEnd);
            var length = end > start ? end - start : TimeSpan.FromHours(24) - (start - end);
            if (length <= TimeSpan.Zero) length = TimeSpan.FromHours(24);

            var step = Step(WindowIntervalValue, WindowIntervalUnit, TimeSpan.FromSeconds(1));
            // 步长大于窗口长度时，每个窗口只跑一次（起点），不会排到窗口外
            if (step >= length) step = length;

            if (!WindowIntervalAnchorDate.HasValue) WindowIntervalAnchorDate = from.Date;
            var anchor = WindowIntervalAnchorDate.Value.Date;
            int daysStep = Math.Max(1, WindowIntervalDaysInterval);

            // 从前一天起扫：跨午夜窗口里，from 所在日期的凌晨点属于"前一天开始的窗口"
            for (int i = 0; i < 400; i++)
            {
                var day = from.Date.AddDays(-1 + i);
                int diff = (day - anchor).Days;
                if (diff < 0 || diff % daysStep != 0) continue;

                var wStart = day.Add(start);
                var wEnd = wStart.Add(length);
                if (wEnd <= from) continue;

                DateTime cand;
                if (wStart > from)
                {
                    cand = wStart;
                }
                else
                {
                    // ceil((from - wStart) / step)，再加 0 个 step 保证严格晚于 from
                    long elapsed = (from - wStart).Ticks;
                    long k = elapsed / step.Ticks;
                    if (wStart.AddTicks(k * step.Ticks) <= from) k++;
                    cand = wStart.AddTicks(k * step.Ticks);
                }
                if (cand >= wEnd) continue;

                WindowIntervalAnchorDate = day;
                return ApplySkipRules(cand);
            }
            return null;
        }

        /// <summary>按单位把数值转成时间间隔，并兜住非正数（最小 1 秒）</summary>
        private static TimeSpan Step(int value, IntervalUnit unit, TimeSpan min)
        {
            var v = Math.Max(0, value);
            var step = unit == IntervalUnit.Hours
                ? TimeSpan.FromHours(v)
                : unit == IntervalUnit.Seconds
                    ? TimeSpan.FromSeconds(v)
                    : TimeSpan.FromMinutes(v);
            return step > min ? step : min;
        }

        /// <summary>把任意 TimeSpan 归一化到 [0, 24h)，便于安全地与 DateTime.Date 相加</summary>
        private static TimeSpan NormalizeToDay(TimeSpan value)
        {
            long ticks = value.Ticks % TimeSpan.TicksPerDay;
            if (ticks < 0) ticks += TimeSpan.TicksPerDay;
            return TimeSpan.FromTicks(ticks);
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
            // 连续间隔
            IntervalValue, (int)IntervalUnit,
            // 日历排期（三个锚点是内部状态，不进签名）
            (int)CalendarDayMode, CalendarTimes.Trim(), CalendarDates.Trim(),
            CalendarDaysInterval,
            CalendarWeekdays == null ? "" : string.Join(",", CalendarWeekdays.Select(d => (int)d).OrderBy(x => x)),
            (int)CalendarMonthlyMode, CalendarMonthlyValue, (int)CalendarMonthlyWeekday,
            // 时段内执行（锚点是内部状态，不进签名）
            (int)WindowMode, WindowStart, WindowEnd, WindowStepValue,
            (int)WindowStepUnit, WindowDaysInterval,
            // 通用微调
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

        private static string UnitText(IntervalUnit unit)
            => unit == IntervalUnit.Hours ? "小时" : unit == IntervalUnit.Seconds ? "秒" : "分钟";

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
                case TimeTriggerKind.Calendar:
                {
                    var times = ParseCalendarTimes();
                    if (times.Count == 0) return null;

                    if (CalendarDayMode == CalendarDayMode.SpecificDates)
                    {
                        var dates = ParseCalendarDates();
                        var candidates = new List<DateTime>();
                        foreach (var d in dates)
                            foreach (var t in times)
                                candidates.Add(d.Add(t));
                        candidates.Sort();
                        for (int i = candidates.Count - 1; i >= 0; i--)
                            if (candidates[i] <= now) return candidates[i];
                        return null;
                    }

                    if (CalendarDayMode == CalendarDayMode.EveryNDays)
                    {
                        int step = Math.Max(1, CalendarDaysInterval);
                        var anchor = CalendarAnchorDate ?? now.Date;
                        for (int i = 0; i < 400; i++)
                        {
                            var day = now.Date.AddDays(-i);
                            int diff = (day - anchor).Days;
                            if (diff < 0 || diff % step != 0) continue;
                            for (int k = times.Count - 1; k >= 0; k--)
                            {
                                var cand = day.Add(times[k]);
                                if (cand <= now) return cand;
                            }
                        }
                        return null;
                    }

                    if (CalendarDayMode == CalendarDayMode.Weekly)
                    {
                        var days = CalendarWeekdays ?? new DayOfWeek[0];
                        for (int i = 0; i < 14; i++)
                        {
                            var day = now.Date.AddDays(-i);
                            if (!Array.Exists(days, d => d == day.DayOfWeek)) continue;
                            for (int k = times.Count - 1; k >= 0; k--)
                            {
                                var cand = day.Add(times[k]);
                                if (cand <= now) return cand;
                            }
                        }
                        return null;
                    }

                    if (CalendarDayMode == CalendarDayMode.Monthly)
                    {
                        var anchor = new DateTime(now.Year, now.Month, 1);
                        for (int i = 0; i <= 24; i++)
                        {
                            var month = anchor.AddMonths(-i);
                            var date = CalendarMonthlyDateIn(month);
                            for (int k = times.Count - 1; k >= 0; k--)
                            {
                                var cand = date.Add(times[k]);
                                if (cand <= now) return cand;
                            }
                        }
                    }
                    return null;
                }

                default:
                    // 连续间隔 / 时段内执行都没有可凭空反推的固定计划点：
                    // 随机点带随机性，窗口内间隔点是按步长在窗口里排的，无法由当前时刻可靠反推
                    //（窗口边界、锚点、步长组合下会算出落在窗口外的点）。
                    // 错过补偿统一依赖存档的 MissedPlanPoint（即 NextRunTime 里那个已过去的点），
                    // 故这里返回 null 交给存档值兜底。
                    return null;
            }
        }

        public string Describe()
        {
            var core = DescribeCore();
            var extra = new List<string>();

            // 自身已带随机 / 固定点语义的方式不叠加抖动（见 SuppressJitter），
            // 不显示误导性的抖动文案；其余方式照旧。
            if (RandomJitterMinutes > 0)
            {
                if (SuppressJitter)
                    extra.Add("该规则自身已确定执行时刻，抖动不叠加");
                else
                    extra.Add($"随机抖动 0–{RandomJitterMinutes} 分钟");
            }

            // 纯间隔方式没有"哪一天"的概念，跳过规则对它无效，描述里也就不该出现
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
                case TimeTriggerKind.Calendar:
                {
                    var times = ParseCalendarTimes();
                    var timeTxt = times.Count == 0
                        ? "未设置"
                        : string.Join("、", times.Select(t => t.ToString(t.Seconds == 0 ? @"hh\:mm" : @"hh\:mm\:ss")));
                    switch (CalendarDayMode)
                    {
                        case CalendarDayMode.SpecificDates:
                        {
                            var dates = ParseCalendarDates();
                            var dateTxt = dates.Count == 0
                                ? "未设置"
                                : string.Join("、", dates.Select(d => d.ToString("yyyy-MM-dd")));
                            return $"指定日期 {dateTxt} 的 {timeTxt}";
                        }
                        case CalendarDayMode.EveryNDays:
                            return $"每隔 {Math.Max(1, CalendarDaysInterval)} 天 {timeTxt}";
                        case CalendarDayMode.Weekly:
                        {
                            var days = CalendarWeekdays ?? new DayOfWeek[0];
                            var txt = days.Length == 0
                                ? "未选"
                                : string.Join("/", days.OrderBy(d => (int)d).Select(d => EnumText.Day(d)));
                            return $"每周 {txt} {timeTxt}";
                        }
                        case CalendarDayMode.Monthly:
                        {
                            string what;
                            switch (CalendarMonthlyMode)
                            {
                                case MonthlyMode.LastDay:
                                    what = "最后一天";
                                    break;
                                case MonthlyMode.NthWeekday:
                                    what = CalendarMonthlyValue >= 5
                                        ? "最后一个" + EnumText.Day(CalendarMonthlyWeekday)
                                        : $"第 {Math.Min(5, Math.Max(1, CalendarMonthlyValue))} 个" + EnumText.Day(CalendarMonthlyWeekday);
                                    break;
                                default:
                                    what = Math.Max(1, CalendarMonthlyValue) + " 日";
                                    break;
                            }
                            return $"每月 {what} {timeTxt}";
                        }
                        default:
                            return "日历排期";
                    }
                }
                case TimeTriggerKind.Window:
                {
                    var s = NormalizeToDay(WindowStart);
                    var e = NormalizeToDay(WindowEnd);
                    var startTxt = s.ToString(@"hh\:mm");
                    var endTxt = e.ToString(@"hh\:mm");
                    // end <= start 表示窗口跨午夜延续到次日，显式标注「次日」避免误读。
                    if (e <= s) endTxt = "次日 " + endTxt;
                    var days = Math.Max(1, WindowDaysInterval);
                    var dayTxt = days == 1 ? "每天" : $"每 {days} 天";
                    if (WindowMode == WindowMode.Random)
                        return $"{dayTxt} {startTxt}–{endTxt} 内随机一点";
                    return $"{dayTxt} {startTxt}–{endTxt} 内每 {Math.Max(1, WindowStepValue)} {UnitText(WindowStepUnit)}";
                }
                case TimeTriggerKind.Interval:
                {
                    var first = "";
                    if (FirstRunMode == IntervalFirstRunMode.Immediately)
                        first = "，首次立即执行";
                    else if (FirstRunMode == IntervalFirstRunMode.AfterDelay)
                        first = $"，首次延迟 {Math.Max(0, FirstRunDelayValue)} {UnitText(FirstRunDelayUnit)}";
                    return $"每隔 {IntervalValue} {UnitText(IntervalUnit)}{first}";
                }
                default:
                    return "时间触发";
            }
        }
    }
}
