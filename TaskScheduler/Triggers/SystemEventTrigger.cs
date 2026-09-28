using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using TaskScheduler.Engine;
using TaskScheduler.Native;

// System.Windows.Forms 里也有一个 Timer，和 System.Threading.Timer 同名；
// 这里需要的是线程池定时器（回调不在 UI 线程、无需消息循环），显式起个别名避免歧义。
using Timer = System.Threading.Timer;

namespace TaskScheduler.Triggers
{
    /// <summary>
    /// 系统事件触发器：系统启动 / 用户登录 / 锁屏 / 解锁 / 空闲 / 电源切换 / 网络变化 /
    /// 注销关机 / 设备接入 / 进程启停 / 远程桌面连接断开。
    ///
    /// 启动与登录在引擎启动时立即触发（本机登录场景近似等同于引擎随登录自启）。
    ///
    /// 两个实现上的取舍：
    /// - 「网络地址变化」带防抖。网卡上下线、DHCP 续约、VPN 建立往往会连着抛好几次事件，
    ///   不合并的话下游任务会被连续拉起（"连上公司网就同步代码"会同步三遍）。
    /// - 「进程启动 / 退出」用轮询而不是 WMI 的 Win32_ProcessStartTrace：
    ///   后者需要管理员权限（内部依赖 SeDebugPrivilege），非管理员运行会直接抛 Access Denied；
    ///   轮询零权限、服务模式下也能用，代价是最大 ProcessPollSeconds 秒的延迟。
    /// </summary>
    public class SystemEventTrigger : ITrigger
    {
        public TriggerType Type => TriggerType.SystemEvent;

        public SystemEventType EventType { get; set; } = SystemEventType.Startup;
        public int IdleMinutes { get; set; } = 5;

        /// <summary>
        /// 进程启动 / 退出事件关注的进程名，多个用逗号分隔，可带 .exe 后缀（会自动去掉）。
        /// 留空表示"任意进程" —— 那会非常频繁（系统每秒都在起进程），一般不建议留空。
        /// </summary>
        public string ProcessNameFilter { get; set; } = "";

        /// <summary>进程轮询间隔（秒），实际生效值不小于 1</summary>
        public int ProcessPollSeconds { get; set; } = 3;

        /// <summary>网络变化防抖窗口（秒）：这段时间内没有新事件才真正触发一次</summary>
        public int NetworkDebounceSeconds { get; set; } = 3;

        public DateTime? NextRunTime => null;

        public event EventHandler<TriggerFiredEventArgs> Fired;

        private bool _started;
        private Timer _idleTimer;
        private bool _idleLatched;

        private Timer _netTimer;
        private readonly object _netLock = new object();

        private Timer _procTimer;
        private HashSet<string> _procSnapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private ManagementEventWatcher _volumeWatcher;

        public void Start()
        {
            if (_started) return;
            _started = true;

            try
            {
                switch (EventType)
                {
                    case SystemEventType.Startup:
                    case SystemEventType.Logon:
                        Raise("系统事件：" + (EventType == SystemEventType.Startup ? "启动" : "登录"));
                        break;

                    case SystemEventType.Lock:
                    case SystemEventType.Unlock:
                    case SystemEventType.RemoteConnect:
                    case SystemEventType.RemoteDisconnect:
                        // 服务在 session 0，没有交互桌面，SystemEvents.SessionSwitch 收不到
                        // 交互会话的锁屏 / 解锁 / 远程事件；这些事件改由界面进程感知后通过管道转发
                        // （SchedulerEngine.InjectSystemEvent）。非交互进程里不订阅，避免和服务端注入双触发。
                        if (!Environment.UserInteractive)
                        {
                            Log.Write("[系统事件] 非交互进程，跳过锁屏/解锁/远程的本地订阅（等待界面转发）");
                            break;
                        }
                        SystemEvents.SessionSwitch += OnSessionSwitch;
                        break;

                    case SystemEventType.Idle:
                        _idleTimer = new Timer(CheckIdle, null, 1000, 2000);
                        break;

                    case SystemEventType.PowerOn:
                    case SystemEventType.PowerOff:
                        SystemEvents.PowerModeChanged += OnPowerModeChanged;
                        break;

                    case SystemEventType.NetworkChanged:
                        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
                        break;

                    case SystemEventType.SessionEnding:
                        // 同样只存在于交互会话：注销 / 关机前。服务进程里不订阅，由界面转发。
                        if (!Environment.UserInteractive)
                        {
                            Log.Write("[系统事件] 非交互进程，跳过 SessionEnding 的本地订阅（等待界面转发）");
                            break;
                        }
                        SystemEvents.SessionEnding += OnSessionEnding;
                        break;

                    case SystemEventType.DeviceArrived:
                        StartVolumeWatch();
                        break;

                    case SystemEventType.ProcessStarted:
                    case SystemEventType.ProcessStopped:
                        StartProcessWatch();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[系统事件] 监听启动失败（{EventType}）：{ex.Message}");
            }
        }

        public void Stop()
        {
            _started = false;

            // SystemEvents 在服务模式下可能压根没挂上，退订一律兜异常
            try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            try { SystemEvents.SessionEnding -= OnSessionEnding; } catch { }
            try { System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged; } catch { }

            if (_idleTimer != null) { _idleTimer.Dispose(); _idleTimer = null; }

            lock (_netLock)
            {
                if (_netTimer != null) { _netTimer.Dispose(); _netTimer = null; }
            }

            if (_procTimer != null) { _procTimer.Dispose(); _procTimer = null; }

            if (_volumeWatcher != null)
            {
                try { _volumeWatcher.EventArrived -= OnVolumeChanged; } catch { }
                try { _volumeWatcher.Stop(); } catch { }
                try { _volumeWatcher.Dispose(); } catch { }
                _volumeWatcher = null;
            }
        }

        // ================= 会话事件（锁屏 / 解锁 / 远程桌面） =================

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                    if (EventType == SystemEventType.Lock) Raise("系统锁屏");
                    break;
                case SessionSwitchReason.SessionUnlock:
                    if (EventType == SystemEventType.Unlock) Raise("系统解锁");
                    break;
                case SessionSwitchReason.RemoteConnect:
                    if (EventType == SystemEventType.RemoteConnect) Raise("远程桌面已连接");
                    break;
                case SessionSwitchReason.RemoteDisconnect:
                    if (EventType == SystemEventType.RemoteDisconnect) Raise("远程桌面已断开");
                    break;
            }
        }

        // ================= 空闲 =================

        private void CheckIdle(object state)
        {
            var idleMs = NativeMethods.GetIdleTimeMilliseconds();
            if (idleMs >= (uint)(IdleMinutes * 60000))
            {
                if (!_idleLatched) { _idleLatched = true; Raise($"空闲超过 {IdleMinutes} 分钟"); }
            }
            else
            {
                _idleLatched = false;
            }
        }

        // ================= 电源 =================

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            // Suspend / Resume 是休眠唤醒，不是插拔电源；只有 StatusChange 才代表供电状态变了
            if (e.Mode != PowerModes.StatusChange) return;

            bool onAc;
            try { onAc = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online; }
            catch { return; }   // 台式机 / 服务模式下取不到状态

            if (EventType == SystemEventType.PowerOn && onAc) Raise("已接入电源（AC）");
            else if (EventType == SystemEventType.PowerOff && !onAc) Raise("已切换到电池供电");
        }

        // ================= 注销 / 关机 =================

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            Raise(e.Reason == SessionEndReasons.Logoff ? "用户注销" : "系统关机");
        }

        // ================= 网络地址变化（带防抖） =================

        private void OnNetworkAddressChanged(object sender, EventArgs e)
        {
            var ms = Math.Max(500, NetworkDebounceSeconds * 1000);
            lock (_netLock)
            {
                // 末尾触发：等网络稳定下来再报，避免一次插网线报出三条
                if (_netTimer == null)
                    _netTimer = new Timer(_ => FlushNetwork(), null, ms, Timeout.Infinite);
                else
                    _netTimer.Change(ms, Timeout.Infinite);
            }
        }

        private void FlushNetwork()
        {
            if (!_started || EventType != SystemEventType.NetworkChanged) return;
            Raise("网络地址发生变化");
        }

        // ================= 设备接入（WMI） =================

        private void StartVolumeWatch()
        {
            try
            {
                _volumeWatcher = new ManagementEventWatcher(
                    new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent"));
                _volumeWatcher.EventArrived += OnVolumeChanged;
                _volumeWatcher.Start();
            }
            catch (Exception ex)
            {
                // WMI 通道在某些精简系统 / 服务账户下不可用。这里只记录，不抛：
                // 抛出去会让整个任务的触发器启动失败，代价比"这条事件暂时不生效"大得多。
                Log.Write($"[系统事件] 设备接入监听不可用（{ex.Message}）");
            }
        }

        private void OnVolumeChanged(object sender, EventArrivedEventArgs e)
        {
            string what = "设备变化";
            try
            {
                var t = Convert.ToInt32(e.NewEvent["EventType"]);
                switch (t)
                {
                    case 1: what = "设备配置变化"; break;
                    case 2: what = "设备接入"; break;
                    case 3: what = "设备移除"; break;
                    case 4: what = "设备停靠"; break;
                }

                try
                {
                    var drive = Convert.ToString(e.NewEvent["DriveName"]);
                    if (!string.IsNullOrWhiteSpace(drive)) what += $"（{drive}）";
                }
                catch { }
            }
            catch { }

            Raise(what);
        }

        // ================= 进程启动 / 退出（轮询） =================

        private void StartProcessWatch()
        {
            _procSnapshot = SnapshotProcesses();
            var period = Math.Max(1, ProcessPollSeconds) * 1000;
            _procTimer = new Timer(CheckProcesses, null, period, period);
        }

        private HashSet<string> SnapshotProcesses()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var filters = SplitList(ProcessNameFilter);

            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    using (p)
                    {
                        try
                        {
                            var n = p.ProcessName;
                            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                n = n.Substring(0, n.Length - 4);

                            if (filters.Length == 0 ||
                                filters.Any(f => string.Equals(f, n, StringComparison.OrdinalIgnoreCase)))
                                set.Add(n);
                        }
                        catch { }   // 单个进程读属性失败（已退出 / 权限不足）跳过
                    }
                }
            }
            catch { }

            return set;
        }

        private void CheckProcesses(object state)
        {
            if (!_started) return;
            try
            {
                var now = SnapshotProcesses();
                var added = now.Where(n => !_procSnapshot.Contains(n)).ToList();
                var removed = _procSnapshot.Where(n => !now.Contains(n)).ToList();
                _procSnapshot = now;

                if (EventType == SystemEventType.ProcessStarted && added.Count > 0)
                    Raise("进程启动：" + Summarize(added));
                else if (EventType == SystemEventType.ProcessStopped && removed.Count > 0)
                    Raise("进程退出：" + Summarize(removed));
            }
            catch { }
        }

        private static string Summarize(List<string> names)
            => names.Count <= 3
                ? string.Join("、", names)
                : string.Join("、", names.Take(3)) + $" 等 {names.Count} 个";

        internal static string[] SplitList(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new string[0];
            return s.Split(new[] { ',', '，', ';', '；', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x =>
                    {
                        var t = x.Trim();
                        if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            t = t.Substring(0, t.Length - 4);
                        return t;
                    })
                    .Where(x => x.Length > 0)
                    .ToArray();
        }

        // ================= 触发与描述 =================

        private void Raise(string msg)
        {
            if (!_started) return;
            Fired?.Invoke(this, new TriggerFiredEventArgs { Message = msg });
        }

        public string Describe()
        {
            var filters = SplitList(ProcessNameFilter);
            var flt = filters.Length == 0 ? "（任意进程）" : "（" + string.Join("/", filters) + "）";

            switch (EventType)
            {
                case SystemEventType.Startup: return "系统启动";
                case SystemEventType.Logon: return "用户登录";
                case SystemEventType.Lock: return "系统锁屏";
                case SystemEventType.Unlock: return "系统解锁";
                case SystemEventType.Idle: return $"空闲 {IdleMinutes} 分钟";
                case SystemEventType.PowerOn: return "接入电源";
                case SystemEventType.PowerOff: return "切换到电池";
                case SystemEventType.NetworkChanged: return "网络地址变化";
                case SystemEventType.SessionEnding: return "注销 / 关机前";
                case SystemEventType.DeviceArrived: return "设备接入";
                case SystemEventType.ProcessStarted: return "进程启动" + flt;
                case SystemEventType.ProcessStopped: return "进程退出" + flt;
                case SystemEventType.RemoteConnect: return "远程桌面连接";
                case SystemEventType.RemoteDisconnect: return "远程桌面断开";
                default: return "系统事件";
            }
        }
    }
}
