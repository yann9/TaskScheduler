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
    /// </summary>
    public class TrayIconManager : IDisposable, INotificationService
    {
        private readonly NotifyIcon _icon;

        public TrayIconManager(Action startEngine, Action stopEngine, Action exitApp)
        {
            _icon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "自动化任务调度器",
                Visible = true
            };
            _icon.DoubleClick += (s, e) => ShowMainWindow();

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("打开主窗口", null, (s, e) => ShowMainWindow()));
            menu.Items.Add(new ToolStripMenuItem("启动引擎", null, (s, e) => startEngine?.Invoke()));
            menu.Items.Add(new ToolStripMenuItem("停止引擎", null, (s, e) => stopEngine?.Invoke()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) => exitApp?.Invoke()));
            _icon.ContextMenuStrip = menu;
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
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
