using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskScheduler.Engine
{
    /// <summary>
    /// 系统休眠阻止（引用计数，可叠加）。
    ///
    /// 两个用途共用这一个核心：
    ///   1) 任务运行期间自动阻止 —— 引擎在动作执行期间 <see cref="Acquire"/>，
    ///      结束时 Dispose（见 SchedulerEngine.RunTaskInnerAsync）；
    ///   2) 用户手动保持 —— 托盘菜单「阻止系统休眠」勾上后无限期持有。
    /// 计数 &gt; 0 期间系统保持"工作状态"（不打自动睡眠），归零立即恢复原策略。
    ///
    /// 实现选型（为什么不是一句 SetThreadExecutionState 就完了）：
    ///   * ES_CONTINUOUS 的生效范围是**调用它的那个线程**，要求清除时必须在同一线程上
    ///     再调一次不带 ES_SYSTEM_REQUIRED 的 SetThreadExecutionState。
    ///     引擎的动作跑在线程池上，每次 Acquire / Dispose 可能落在不同线程，
    ///     直接在各处调用会"谁拿的谁丢了"—— 清不掉或者误清别人的。
    ///   * 所以用一条专职 Keeper 线程统一持有 / 释放：Acquire 只做计数 + 唤醒，
    ///     真正的 API 调用只发生在 Keeper 线程上，天然满足"同线程清除"约束。
    ///   * 没用 PowerSetRequest（服务场景的另一种正统做法）：它要组 REASON_CONTEXT
    ///     结构体，而本程序的本地引擎就在交互会话里，SetThreadExecutionState 足够；
    ///     后台服务（session 0）里该 API 同样有效 —— 系统空闲计时器是全机的，
    ///     不区分调用线程在哪个会话。
    ///
    /// 生命周期说明：Keeper 是后台线程，进程退出时随之消亡 —— 操作系统会在进程终止时
    /// 自动清掉它持有的执行状态，不存在"程序崩了机器永远不睡"的泄漏。
    /// </summary>
    public static class SleepGuard
    {
        [Flags]
        private enum EXECUTION_STATE : uint
        {
            ES_CONTINUOUS = 0x80000000,
            ES_SYSTEM_REQUIRED = 0x00000001,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        private static readonly object _lock = new object();
        private static int _refs;
        private static Thread _keeper;

        /// <summary>当前是否在阻止系统休眠（任一持有者存在即为 true）</summary>
        public static bool IsHolding { get; private set; }

        /// <summary>
        /// 持有状态在 0↔1 之间翻转时触发（Keeper 线程上，非 UI 线程）。
        /// 供需要展示"现在为什么不睡"的界面同步状态。
        /// </summary>
        public static event EventHandler Changed;

        /// <summary>
        /// 获取一个持有句柄。Dispose 即释放。同一时刻可以有任意多个持有者叠加，
        /// 只有最后一个释放才会真正恢复系统休眠策略。
        /// 任何异常都不该打断任务执行 —— 失败只记日志，返回的句柄 Dispose 时幂等。
        /// </summary>
        public static IDisposable Acquire(string reason)
        {
            lock (_lock)
            {
                _refs++;
                EnsureKeeper();
                Monitor.PulseAll(_lock);
            }
            return new Handle(string.IsNullOrWhiteSpace(reason) ? "未注明" : reason);
        }

        private static void EnsureKeeper()
        {
            // 线程若意外消亡（理论上进不了持锁路径，防御一下），重建一条接续持有
            if (_keeper != null && _keeper.IsAlive) return;
            _keeper = new Thread(KeeperLoop) { IsBackground = true, Name = "SleepGuardKeeper" };
            _keeper.Start();
        }

        private static void KeeperLoop()
        {
            while (true)
            {
                lock (_lock)
                {
                    while (_refs == 0) Monitor.Wait(_lock);
                }

                var st = SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED);
                if ((uint)st == 0)
                    Log.Error("设置 ES_SYSTEM_REQUIRED 失败（GetLastError=" + Marshal.GetLastWin32Error() + "），本次不阻止休眠");

                SetHolding(true);

                lock (_lock)
                {
                    while (_refs > 0) Monitor.Wait(_lock);
                }

                // 必须回到同一线程清除：不带 ES_SYSTEM_REQUIRED 的 ES_CONTINUOUS 调用
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
                SetHolding(false);
            }
        }

        private static void SetHolding(bool holding)
        {
            if (IsHolding != holding)
            {
                IsHolding = holding;
                Log.Write(holding
                    ? "[休眠阻止] 开始保持系统唤醒（任务运行中或已手动开启）"
                    : "[休眠阻止] 所有持有已释放，恢复系统默认休眠策略");
            }
            var h = Changed;
            if (h != null) { try { h(null, EventArgs.Empty); } catch { } }
        }

        /// <summary>释放句柄。重复 Dispose 安全。</summary>
        private sealed class Handle : IDisposable
        {
            private readonly string _reason;
            private bool _released;

            public Handle(string reason) { _reason = reason; }

            public void Dispose()
            {
                lock (_lock)
                {
                    if (_released) return;
                    _released = true;
                    _refs = Math.Max(0, _refs - 1);
                    Monitor.PulseAll(_lock);
                }
            }
        }
    }
}
