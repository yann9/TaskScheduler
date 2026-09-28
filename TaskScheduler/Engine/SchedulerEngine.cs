using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using TaskScheduler.Actions;
using TaskScheduler.Conditions;
using TaskScheduler.Persistence;
using TaskScheduler.Triggers;

namespace TaskScheduler.Engine
{
    /// <summary>
    /// 调度引擎：负责加载/保存任务、启停触发器、触发即执行对应动作。
    /// </summary>
    public class SchedulerEngine
    {
        private readonly INotificationService _notifier;
        private readonly Dispatcher _dispatcher;
        private readonly TaskRepository _repo = new TaskRepository();
        private readonly List<AutomationTask> _tasks = new List<AutomationTask>();
        private readonly object _lock = new object();

        /// <summary>
        /// 正在执行的任务（含条件等待窗口期）。同一任务的两次触发不允许叠加：
        /// 间隔小于动作耗时时（1 分钟间隔 + 跑 5 分钟的脚本）会堆叠出好几份进程，
        /// 手动「立即执行」撞上定时触发同样如此。
        /// </summary>
        private readonly HashSet<AutomationTask> _busyTasks = new HashSet<AutomationTask>();

        /// <summary>
        /// 传给动作的取消令牌。Stop() 时取消 —— 否则"停止引擎"只停了触发器，
        /// 正在跑的脚本（可能挂死）会一直留着，用户在界面上看不到任何变化。
        /// </summary>
        private volatile CancellationTokenSource _runCts = new CancellationTokenSource();

        private bool _running;

        public bool IsRunning => _running;

        /// <summary>
        /// 运行记录（环形，保留条数由设置决定）。
        /// 本地托管模式下界面直接读它；服务模式下界面通过管道取。
        /// </summary>
        public RunHistoryStore History { get; } = new RunHistoryStore();

        /// <summary>最近一次加载存档的告警（文件损坏 / 从备份恢复），供界面提示用户</summary>
        public string LastLoadWarning => _repo.LastLoadWarning;

        public event EventHandler<TaskRunEventArgs> TaskRun;

        public SchedulerEngine(INotificationService notifier, Dispatcher dispatcher)
        {
            _notifier = notifier;
            _dispatcher = dispatcher;
        }

        public IReadOnlyList<AutomationTask> Tasks
        {
            get { lock (_lock) return _tasks.ToList(); }
        }

        public void Load()
        {
            var loaded = _repo.Load();
            lock (_lock) { _tasks.Clear(); _tasks.AddRange(loaded); }
            RaiseRun($"已加载 {loaded.Count} 个任务");
        }

        public void Save()
        {
            lock (_lock) _repo.Save(_tasks);
        }

        public void AddTask(AutomationTask task)
        {
            lock (_lock) _tasks.Add(task);
            // 引擎已在运行时，新增/改过的任务必须立刻重新排期；
            // 否则它要等到下一次引擎启动（重启程序或开机）才会真正生效。
            if (_running && task != null && task.Enabled && task.Trigger != null) StartTask(task);
            Save();
        }

        public void RemoveTask(AutomationTask task)
        {
            StopTask(task);
            lock (_lock) _tasks.Remove(task);
            Save();
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            lock (_lock)
            {
                foreach (var t in _tasks.Where(x => x.Enabled && x.Trigger != null))
                    StartTask(t);
            }
            RaiseRun("引擎已启动");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            lock (_lock)
            {
                foreach (var t in _tasks) StopTask(t);
            }

            // "停止引擎"的语义就是让它现在停下：正在执行的动作一并取消。
            // 换一个新的 CTS 而不是 Dispose 旧的（可能还有任务在引用它）。
            var oldCts = _runCts;
            _runCts = new CancellationTokenSource();
            try { oldCts.Cancel(); } catch { }

            Save(); // 把最终的下次执行时间落盘，下次启动才能沿用
            RaiseRun("引擎已停止");
        }

        /// <summary>启动单个任务的触发器</summary>
        public void StartTask(AutomationTask task)
        {
            if (task.Trigger == null) return;
            task.Trigger.Fired -= OnFired;
            task.Trigger.Fired += OnFired;

            // 排期变化要写回存档：下次执行时间只存在内存里的话，重启就丢了，
            // "间隔"任务会重新从零计时（重启比间隔频繁时永远等不到）。
            var tt = task.Trigger as TimeTrigger;
            if (tt != null)
            {
                tt.ScheduleChanged -= OnScheduleChanged;
                tt.ScheduleChanged += OnScheduleChanged;
            }

            try
            {
                task.Trigger.Start();
                task.NextRunTime = task.Trigger.NextRunTime;
            }
            catch (Exception ex) { Log.Error($"启动触发器失败[{task.Name}]：{ex.Message}"); }

            TryMakeUpMissedRun(task);
        }

        /// <summary>触发器重排了下次执行时间 → 同步到任务并写回存档（值没变则不白写磁盘）</summary>
        private void OnScheduleChanged(object sender, EventArgs e)
        {
            var tt = sender as TimeTrigger;
            if (tt == null) return;
            try
            {
                AutomationTask task;
                lock (_lock) task = _tasks.FirstOrDefault(t => t.Trigger == tt);
                if (task == null) return;
                if (task.NextRunTime == tt.NextRunTime) return;
                task.NextRunTime = tt.NextRunTime;
                Save();
            }
            catch (Exception ex) { Log.Error($"保存排期失败：{ex.Message}"); }
        }

        /// <summary>
        /// 错过补偿：任务勾了 RunIfMissed，且上一个计划点已过、那次还没执行过，就补跑一次。
        /// 默认不补（同 cron / systemd timer / 任务计划），避免开机瞬间集中补跑一堆任务。
        /// 计划点来自触发器存档的下次执行时间，因此"间隔"任务同样适用。
        /// 用 Task.Run 派发，避免在 Start() 的锁内同步执行条件校验（网络条件可能阻塞）。
        /// </summary>
        private void TryMakeUpMissedRun(AutomationTask task)
        {
            if (task == null || !(task.Trigger is TimeTrigger tt) || !tt.RunIfMissed) return;
            var prev = tt.ComputePrevious(DateTime.Now);
            if (prev == null) return;
            if (task.LastRunTime != null && task.LastRunTime.Value >= prev.Value) return;

            Log.Write($"[{task.Name}] 错过补偿：计划点 {prev.Value:yyyy-MM-dd HH:mm} 未执行，立即补跑一次");
            _ = Task.Run(() => RunTaskAsync(task, $"错过补偿（{prev.Value:MM-dd HH:mm}）"));
        }

        /// <summary>停止单个任务的触发器</summary>
        public void StopTask(AutomationTask task)
        {
            if (task.Trigger == null) return;
            task.Trigger.Fired -= OnFired;
            var tt = task.Trigger as TimeTrigger;
            if (tt != null) tt.ScheduleChanged -= OnScheduleChanged;
            try { task.Trigger.Stop(); }
            catch { }
        }

        private void OnFired(object sender, TriggerFiredEventArgs e)
        {
            ITrigger trigger = sender as ITrigger;
            AutomationTask task = null;
            lock (_lock) task = _tasks.FirstOrDefault(t => t.Trigger == trigger);
            if (task == null) return;
            _ = RunTaskAsync(task, e.Message);
        }

        /// <summary>
        /// 立即执行某个任务（手动或触发器触发入口）。
        ///
        /// 所有调用点都是 fire-and-forget（触发器回调、手动执行、错过补偿），
        /// 所以这里必须自己兜住异常：漏出去就是"任务没跑、日志里也没有"的静默失败。
        /// </summary>
        public async Task RunTaskAsync(AutomationTask task, string triggerMessage = null)
        {
            if (task == null) return;
            try
            {
                await RunTaskInnerAsync(task, triggerMessage);
            }
            catch (Exception ex)
            {
                task.LastRunResult = "异常：" + ex.Message;
                RaiseRun($"[{task.Name}] 执行异常：{ex.Message}", task);
            }
        }

        private async Task RunTaskInnerAsync(AutomationTask task, string triggerMessage)
        {
            // 运行记录在这里统一收口：不管走哪条分支（未配置动作 / 并发跳过 / 条件不满足 /
            // 成功 / 失败 / 被取消），外层 finally 一定会留下一条。
            // 以前"跳过了"只写进日志文件，用户在界面上看不到，恰恰是最难排查的那一类。
            var record = new RunRecord
            {
                TaskId = task.Id,
                TaskName = task.Name,
                StartTime = DateTime.Now,
                Trigger = string.IsNullOrWhiteSpace(triggerMessage) ? "自动触发" : triggerMessage,
                Outcome = RunOutcome.Skipped,
                Message = "未执行"
            };
            var output = new StringBuilder();

            try
            {
                if (task.Action == null)
                {
                    task.LastRunResult = "未配置动作";
                    record.Message = "未配置动作，未执行";
                    return;
                }

                if (!TryBeginRun(task, out var busyReason))
                {
                    task.LastRunResult = "跳过：" + busyReason;
                    record.Message = "跳过：" + busyReason;
                    RaiseRun($"[{task.Name}] {busyReason}，本次触发跳过（避免同一任务叠加运行）", task);
                    return;
                }

                try
                {
                    // 条件校验：触发后、动作前需全部满足，否则跳过。
                    // 配了「条件等待窗口」的任务，不满足时会在窗口内反复重试（见 CheckConditionsAsync）。
                    var failed = await CheckConditionsAsync(task);
                    if (failed != null)
                    {
                        task.LastRunResult = "跳过：条件不满足(" + failed + ")";
                        record.Message = "跳过：条件不满足（" + failed + "）";
                        RaiseRun($"[{task.Name}] 条件不满足，跳过：{failed}", task);
                        task.NextRunTime = task.Trigger?.NextRunTime;
                        return;
                    }

                    var ctx = new ActionContext
                    {
                        Notifier = _notifier,
                        Dispatcher = _dispatcher,
                        TriggerMessage = triggerMessage,
                        Output = output
                    };

                    var start = DateTime.Now;
                    task.LastRunTime = start;
                    try
                    {
                        await task.Action.ExecuteAsync(ctx, _runCts.Token);
                        task.LastRunResult = "成功";
                        record.Outcome = RunOutcome.Success;
                        record.Message = "执行成功";
                        RaiseRun($"[{task.Name}] 执行成功（{(DateTime.Now - start).TotalSeconds:F1}s）", task);
                    }
                    catch (OperationCanceledException)
                    {
                        // 用户点了"停止引擎"，不是任务本身的故障，单独归类
                        task.LastRunResult = "已取消";
                        record.Outcome = RunOutcome.Canceled;
                        record.Message = "执行中被「停止引擎」取消";
                        RaiseRun($"[{task.Name}] 执行已取消", task);
                    }
                    catch (Exception ex)
                    {
                        task.LastRunResult = "失败：" + ex.Message;
                        record.Outcome = RunOutcome.Failed;
                        record.Message = "失败：" + ex.Message;
                        RaiseRun($"[{task.Name}] 执行失败：{ex.Message}", task);
                        NotifyFailure(task, ex.Message);
                    }
                    finally
                    {
                        task.NextRunTime = task.Trigger?.NextRunTime;
                    }
                }
                finally
                {
                    EndRun(task);
                }
            }
            finally
            {
                record.EndTime = DateTime.Now;
                record.DurationMs = Math.Max(0, (record.EndTime - record.StartTime).TotalMilliseconds);
                record.Output = output.ToString();
                PushRecord(record);
            }
        }

        /// <summary>
        /// 写入运行记录。
        /// 设置里关掉"记录成功运行"时，成功的那条直接丢弃 ——
        /// 一个每分钟跑一次的任务，成功记录几天就能把额度占满，把真正要看的失败挤出去。
        /// </summary>
        private void PushRecord(RunRecord record)
        {
            try
            {
                if (record.Outcome == RunOutcome.Success && !SettingsService.Current.RecordSuccessfulRuns) return;
                History.Add(record);
            }
            catch (Exception ex)
            {
                Log.Error("写入运行记录失败：" + ex.Message);
            }
        }

        /// <summary>任务失败时弹气泡提醒（可在设置里关掉）。服务模式没有交互桌面，退化为写日志。</summary>
        private void NotifyFailure(AutomationTask task, string message)
        {
            try
            {
                if (!SettingsService.Current.NotifyOnTaskFailure) return;
                if (_notifier == null) return;

                var title = "任务执行失败：" + task.Name;
                if (_dispatcher != null && !_dispatcher.CheckAccess())
                    _dispatcher.BeginInvoke(new Action(() => { try { _notifier.ShowNotification(title, message); } catch { } }));
                else
                    _notifier.ShowNotification(title, message);
            }
            catch { }
        }

        private bool TryBeginRun(AutomationTask task, out string reason)
        {
            lock (_busyTasks)
            {
                if (_busyTasks.Contains(task))
                {
                    reason = "上一次执行尚未结束";
                    return false;
                }
                _busyTasks.Add(task);
                reason = null;
                return true;
            }
        }

        private void EndRun(AutomationTask task)
        {
            lock (_busyTasks) _busyTasks.Remove(task);
        }

        /// <summary>
        /// 校验全部条件，返回第一条不满足条件的描述；全部满足返回 null。
        ///
        /// 配置了 ConditionWaitSeconds &gt; 0 的任务不会"一次判死"：窗口内每 ConditionRetrySeconds 秒
        /// 重新求值一遍。真正需要等待的场景（网卡刚起来还没拿到 IP、刚拔掉电源、上游文件还在写）
        /// 通常几十秒内就能满足；一次不满足就跳过，代价是整个执行周期被白白错过。
        /// </summary>
        private async Task<string> CheckConditionsAsync(AutomationTask task)
        {
            var conds = task.Conditions;
            if (conds == null || conds.Count == 0) return null;

            var wait = Math.Max(0, task.ConditionWaitSeconds);
            var step = Math.Max(1, task.ConditionRetrySeconds);
            var deadline = DateTime.Now.AddSeconds(wait);
            bool notified = false;

            while (true)
            {
                // 每轮都重新取快照：条件是 ObservableCollection，用户随时可能在界面上增删，
                // 而求值跑在线程池上 —— 直接枚举会被"枚举中修改集合"打中。
                var snapshot = SnapshotConditions(conds);
                if (snapshot == null)
                {
                    if (wait <= 0 || DateTime.Now >= deadline) return "条件列表正在被修改";
                    await Task.Delay(TimeSpan.FromSeconds(step));
                    continue;
                }

                // 条件求值一律丢到线程池：
                // 触发器的回调线程可能是定时器线程 / 系统事件线程，手动运行更是直接在 UI 线程上；
                // 条件里可能有 HTTP 探测、服务查询这类会阻塞上百毫秒甚至十几秒的操作，
                // 同步跑会把 UI 卡住（在 UI 线程上做同步 HTTP 等待更会直接死锁）。
                var failed = await Task.Run(() => FirstFailing(snapshot));
                if (failed == null) return null;
                if (wait <= 0 || DateTime.Now >= deadline) return failed;

                if (!notified)
                {
                    notified = true;
                    RaiseRun($"[{task.Name}] 条件暂未满足（{failed}），在 {wait} 秒窗口内等待重试…", task);
                }

                // 最后一轮不要睡过 deadline：等满窗口就立刻判定，不做无谓的空转
                var remain = (deadline - DateTime.Now).TotalSeconds;
                var sleep = Math.Max(1, Math.Min(step, remain));
                await Task.Delay(TimeSpan.FromSeconds(sleep));
            }
        }

        /// <summary>
        /// 取条件列表快照。集合正在被界面线程修改时枚举会抛 InvalidOperationException，
        /// 短暂重试几次；仍失败返回 null 交由调用方按等待窗口处理。
        /// </summary>
        private static ICondition[] SnapshotConditions(IEnumerable<ICondition> conditions)
        {
            for (int i = 0; i < 3; i++)
            {
                try { return conditions.ToArray(); }
                catch (InvalidOperationException) { Thread.Sleep(20); }
                catch { return null; }
            }
            return null;
        }

        /// <summary>返回第一条求值为 false 的条件描述（条件自身的异常已在 ConditionBase 内兜住）</summary>
        private static string FirstFailing(ICondition[] conditions)
        {
            foreach (var c in conditions)
            {
                if (c == null) continue;
                if (!c.Evaluate()) return c.Describe;
            }
            return null;
        }

        private void RaiseRun(string message, AutomationTask task = null)
        {
            Log.Write(message);
            TaskRun?.Invoke(this, new TaskRunEventArgs { Task = task, Message = message });
        }
    }

    public class TaskRunEventArgs : EventArgs
    {
        public AutomationTask Task { get; set; }
        public string Message { get; set; } = "";
    }
}
