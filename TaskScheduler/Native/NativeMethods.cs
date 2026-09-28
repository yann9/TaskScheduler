using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TaskScheduler.Native
{
    /// <summary>Windows API 封装（空闲检测、鼠标模拟、CPU / 内存、前台窗口）</summary>
    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        /// <summary>自上次用户输入以来的空闲毫秒数</summary>
        public static uint GetIdleTimeMilliseconds()
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref lii))
            {
                var tick = (uint)Environment.TickCount;
                var last = lii.dwTime;
                uint diff = tick >= last ? tick - last : uint.MaxValue - last + tick;
                return diff;
            }
            return 0;
        }

        /// <summary>在指定屏幕坐标执行一次鼠标左键点击</summary>
        public static void MouseClick(int x, int y)
        {
            SetCursorPos(x, y);
            mouse_event(MOUSEEVENTF_LEFTDOWN, (uint)x, (uint)y, 0, 0);
            mouse_event(MOUSEEVENTF_LEFTUP, (uint)x, (uint)y, 0, 0);
        }

        // ================= CPU 占用 =================
        //
        // 用 GetSystemTimes 而不是 PerformanceCounter：
        // PerformanceCounter("Processor", "% Processor Time") 首次调用要建计数器、还要预热两次采样，
        // 而且在部分被精简过的系统 / 容器里性能计数器库根本不可用（会抛 InvalidOperationException）。
        // GetSystemTimes 是裸 API，零依赖、零权限，代价只是要自己算两次采样的差值。

        [StructLayout(LayoutKind.Sequential)]
        private struct NATIVE_FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out NATIVE_FILETIME idleTime,
                                                  out NATIVE_FILETIME kernelTime,
                                                  out NATIVE_FILETIME userTime);

        private static readonly object _cpuLock = new object();
        private static ulong _prevIdle, _prevKernel, _prevUser;
        private static bool _hasCpuSample;

        /// <summary>
        /// 全机 CPU 占用百分比（0–100）；采样失败返回 -1。
        ///
        /// 需要两个时间点的差值，所以第一次调用会阻塞约 200ms 取第二个采样点；
        /// 之后再调用就直接复用上一次采样 —— 也就是说连续两次调用之间的间隔越长，
        /// 算出来的是"这段时间内的平均占用率"，反而比瞬时值更平滑、更适合做"机器忙不忙"的判断。
        /// </summary>
        public static double GetCpuUsagePercent()
        {
            lock (_cpuLock)
            {
                if (!TrySampleCpu(out var idle, out var kernel, out var user)) return -1;

                if (!_hasCpuSample)
                {
                    Thread.Sleep(200);
                    _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
                    if (!TrySampleCpu(out idle, out kernel, out user)) return -1;
                }

                var dIdle = idle - _prevIdle;
                var dTotal = (kernel - _prevKernel) + (user - _prevUser);
                _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
                _hasCpuSample = true;

                if (dTotal == 0) return 0;
                // kernel 时间里已经包含 idle，所以 total = kernel + user，busy = total - idle
                var busy = dTotal > dIdle ? dTotal - dIdle : 0;
                var pct = busy * 100.0 / dTotal;
                return pct < 0 ? 0 : (pct > 100 ? 100 : pct);
            }
        }

        private static bool TrySampleCpu(out ulong idle, out ulong kernel, out ulong user)
        {
            idle = kernel = user = 0;
            try
            {
                if (!GetSystemTimes(out var i, out var k, out var u)) return false;
                idle = ToUInt64(i);
                kernel = ToUInt64(k);
                user = ToUInt64(u);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static ulong ToUInt64(NATIVE_FILETIME ft)
            => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

        // ================= 物理内存 =================

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        /// <summary>可用物理内存（MB）；失败返回 -1</summary>
        public static long GetAvailablePhysicalMemoryMB()
        {
            try
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (!GlobalMemoryStatusEx(ref m)) return -1;
                return (long)(m.ullAvailPhys / 1024 / 1024);
            }
            catch
            {
                return -1;
            }
        }

        // ================= 前台窗口 =================

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        /// <summary>前台窗口标题；拿不到（服务模式 / Session 0）返回 null</summary>
        public static string GetForegroundWindowTitle()
        {
            try
            {
                var h = GetForegroundWindow();
                if (h == IntPtr.Zero) return null;
                var sb = new StringBuilder(512);
                var n = GetWindowTextW(h, sb, sb.Capacity);
                return n > 0 ? sb.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 前台窗口是否占满整个显示器。
        /// 桌面（Progman / WorkerW）与任务栏（Shell_TrayWnd）要排除 ——
        /// 它们天生就是"铺满屏幕"的，否则一回到桌面就会被误判成全屏。
        /// </summary>
        public static bool IsForegroundWindowFullScreen()
        {
            try
            {
                var h = GetForegroundWindow();
                if (h == IntPtr.Zero) return false;

                var cls = new StringBuilder(256);
                if (GetClassNameW(h, cls, cls.Capacity) > 0)
                {
                    var c = cls.ToString();
                    if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;
                }

                if (!GetWindowRect(h, out var r)) return false;

                var mon = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero) return false;

                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(mon, ref mi)) return false;

                // 容忍 2px 误差：某些全屏工具会留 1px 边框
                const int tol = 2;
                return Math.Abs(r.left - mi.rcMonitor.left) <= tol
                    && Math.Abs(r.top - mi.rcMonitor.top) <= tol
                    && Math.Abs(r.right - mi.rcMonitor.right) <= tol
                    && Math.Abs(r.bottom - mi.rcMonitor.bottom) <= tol;
            }
            catch
            {
                return false;
            }
        }

        // ================= 单实例：唤起已运行的主窗口 =================
        //
        // 命名事件（EventWaitHandle）在同权限级别下最可靠，但有两个失效场景：
        //   1) 对方以更高完整性级别运行（UAC 提权）→ 打开事件句柄被拒；
        //   2) UIPI 会拦下低级别进程发给高级别窗口的窗口消息。
        // 所以这里再补一条"直接找窗口"的通道：只看窗口标题，隐藏（最小化到托盘）的窗口同样找得到。
        // 没有它的话，那种情况下用户双击图标会"什么都没发生"，也就是"程序再也打不开了"。

        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;
        private const int HWND_BROADCAST = 0xFFFF;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessageW(string lpString);

        /// <summary>
        /// "请显示主窗口"的自定义窗口消息 id。由 RegisterWindowMessage 分配，全系统唯一，
        /// 因此不会与任何系统消息冲突；没有监听它的窗口就是一条无害的广播。
        /// </summary>
        public static uint ShowWindowMessageId { get; } =
            RegisterWindowMessageW("TaskScheduler.ShowMainWindow.v1");

        /// <summary>
        /// "请求退出"的自定义窗口消息 id。
        ///
        /// ⚠ 与安装脚本 TaskScheduler.nsi 里的 <c>TS_EXIT_MESSAGE</c> 常量（0x805A）**必须保持一致**，
        /// 改一处就要改另一处。安装器用
        /// <c>PostMessage(HWND_BROADCAST, 0x805A, 0, 0)</c> 广播，本程序的窗口钩子收到后
        /// 走完整的 Stop() + Save() 再退出。
        ///
        /// 为什么用固定值而不是 RegisterWindowMessage：安装器侧只需要发一次消息，
        /// 固定值让 Pascal Script 零依赖；$8000..$BFFF（WM_APP 段）本来就是留给应用
        /// 自己用的，不会与系统消息冲突。
        ///
        /// 这条通道的价值：**不启动任何子进程**就能请运行中的实例退出。
        /// 安装器执行 TaskScheduler.exe --exit 依赖对方的版本认识这个开关 ——
        /// 对方是旧版本时反而会再拉起一个界面实例，把安装拖住（踩过）。
        /// </summary>
        public const int ExitRequestMessageId = 0x805A;

        /// <summary>向所有顶层窗口广播"显示主窗口"（已有实例的钩子会响应）</summary>
        public static void BroadcastShowMessage()
        {
            try { PostMessageW((IntPtr)HWND_BROADCAST, ShowWindowMessageId, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }

        /// <summary>按标题判断窗口是否存在（用于判断另一个实例到底有没有可唤起的窗口）</summary>
        public static bool TryFindWindow(string title)
        {
            try { return FindWindowW(null, title) != IntPtr.Zero; }
            catch { return false; }
        }

        /// <summary>按标题找到窗口并强制恢复显示；找到并处理了返回 true</summary>
        public static bool TryForceShowWindow(string title)
        {
            try
            {
                var h = FindWindowW(null, title);
                if (h == IntPtr.Zero) return false;

                ShowWindow(h, SW_SHOW);
                ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
