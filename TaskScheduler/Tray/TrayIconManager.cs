using System;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using TaskScheduler.Engine;

namespace TaskScheduler.Tray
{
    /// <summary>
    /// 系统托盘图标管理：同时实现 INotificationService（气泡通知）。
    /// 引擎启停与退出通过回调注入，避免强依赖 App.Engine（服务模式无本地引擎）。
    ///
    /// 菜单不是"建完就不管"的：启动 / 停止引擎两项的可用状态必须跟着引擎真实状态走，
    /// 由 <see cref="UpdateEngineMenu"/> 在引擎状态变化时刷新（主窗口的定时刷新 / App 的
    /// 托盘回调都会调它）。否则引擎明明在跑，右键里「启动引擎」还能点，看起来就是状态错乱。
    /// </summary>
    public class TrayIconManager : IDisposable, INotificationService
    {
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _startEngineItem;
        private readonly ToolStripMenuItem _stopEngineItem;
        private readonly ToolStripMenuItem _preventSleepItem;

        /// <summary>托盘手动「阻止系统休眠」持有的句柄（勾上 = 持有，取消 = 释放）</summary>
        private IDisposable _manualSleepHold;

        public TrayIconManager(Action startEngine, Action stopEngine, Action exitApp)
        {
            _icon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "自动化任务调度器",
                Visible = true
            };
            _icon.DoubleClick += (s, e) => ShowMainWindow();

            _startEngineItem = new ToolStripMenuItem("启动引擎", null, (s, e) => startEngine?.Invoke());
            _stopEngineItem = new ToolStripMenuItem("停止引擎", null, (s, e) => stopEngine?.Invoke());

            // 手动休眠阻止：与"任务运行期间自动阻止"共用同一个引用计数核心（SleepGuard），
            // 勾上是无限期持有，取消勾选即释放。任务执行期间的持有不受这里影响 ——
            // 引擎那一手还在计数里，机器照样不睡。
            _preventSleepItem = new ToolStripMenuItem("阻止系统休眠") { CheckOnClick = true };
            _preventSleepItem.CheckedChanged += (s, e) =>
            {
                if (_preventSleepItem.Checked)
                {
                    try { _manualSleepHold = SleepGuard.Acquire("托盘手动开关"); }
                    catch (Exception ex)
                    {
                        _preventSleepItem.Checked = false;
                        Log.Error("启用休眠阻止失败：" + ex.Message);
                    }
                }
                else
                {
                    _manualSleepHold?.Dispose();
                    _manualSleepHold = null;
                }
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("打开主窗口", null, (s, e) => ShowMainWindow()));
            menu.Items.Add(_startEngineItem);
            menu.Items.Add(_stopEngineItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_preventSleepItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) => exitApp?.Invoke()));
            _icon.ContextMenuStrip = menu;
        }

        /// <summary>
        /// 同步「启动引擎 / 停止引擎」两项的可用状态。引擎在跑 → 只能停；停着 → 只能起。
        /// 主窗口的状态刷新定时器每 2~3 秒会推一次，托盘自己的启停回调也会即时推一次。
        /// </summary>
        public void UpdateEngineMenu(bool engineRunning)
        {
            _startEngineItem.Enabled = !engineRunning;
            _stopEngineItem.Enabled = engineRunning;
        }

        /// <summary>从 exe 提取应用图标（单文件发布下也有效），失败退回系统默认。</summary>
        private static Icon LoadAppIcon()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                {
                    using var raw = Icon.ExtractAssociatedIcon(exe);
                    if (raw != null) return (Icon)raw.Clone();
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        public void Show() => _icon.Visible = true;

        public void Hide() => _icon.Visible = false;

        public void ShowNotification(string title, string message)
            => _icon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);

        private void ShowMainWindow()
        {
            var mw = System.Windows.Application.Current.MainWindow as System.Windows.Window;
            if (mw != null)
            {
                mw.Show();
                mw.WindowState = System.Windows.WindowState.Normal;
                mw.Activate();
            }
        }

        public void Dispose()
        {
            _manualSleepHold?.Dispose();
            _manualSleepHold = null;
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
