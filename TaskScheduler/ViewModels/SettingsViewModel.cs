using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskScheduler.Engine;
using TaskScheduler.Views;

namespace TaskScheduler.ViewModels
{
    /// <summary>
    /// 设置窗口。
    ///
    /// 编辑的是一份**副本**：点「取消」时副本直接丢弃，不会留下半截改动。
    /// 如果直接改 SettingsService.Current，用户勾一下就是即时生效，那"取消"按钮就成了摆设。
    /// 点「确定」时把副本的值抄回单例并落盘。
    /// </summary>
    public partial class SettingsViewModel : ObservableObject
    {
        /// <summary>正在编辑的设置副本（窗口里所有绑定都打它）</summary>
        public AppSettings Settings { get; }

        /// <summary>
        /// 开机自启单独处理：它是注册表状态（HKCU\...\Run），不是文件里的一个字段 ——
        /// 只改 Settings.AutoStartWithWindows 是不够的，得真的去写 / 删注册表项。
        /// </summary>
        [ObservableProperty] private bool _autoStartWithWindows;

        public List<CloseAction> CloseActions { get; } = new List<CloseAction>
        {
            CloseAction.MinimizeToTray, CloseAction.ExitApp
        };

        public List<int> RunRecordOptions { get; } = new List<int> { 50, 100, 200, 500, 1000, 2000 };
        public List<int> LogLineOptions { get; } = new List<int> { 100, 200, 500, 1000, 2000, 5000 };
        public List<int> LogSizeOptions { get; } = new List<int> { 1, 2, 5, 10, 20, 50 };
        public List<int> FontSizeOptions { get; } = new List<int> { 10, 11, 12, 13, 14, 16 };

        public ICommand RestoreDefaultsCommand { get; }

        public SettingsViewModel()
        {
            Settings = SettingsService.Current.Clone();
            // 以注册表的实际状态为准：设置文件里那两个字段可能早就过时了
            // （用户可能在安装器里勾的正启，也可能直接改过注册表）。
            _autoStartWithWindows = AutoStartRegistration.IsEnabled();
            Settings.AutoStartMinimized = AutoStartRegistration.IsMinimizedOnStart();
            RestoreDefaultsCommand = new RelayCommand(RestoreDefaults);
        }

        private void RestoreDefaults()
        {
            var r = TsDialog.Show(
                "把所有设置恢复为默认值？\n\n（开机自启不受影响，需要你另外取消勾选。）",
                "确认", TsDialogButtons.YesNo, TsDialogIcon.Question);
            if (r != System.Windows.MessageBoxResult.Yes) return;

            Settings.CopyFrom(new AppSettings());
        }

        /// <summary>点「确定」后由主窗口调用：抄回单例并落盘，同时同步注册表自启项</summary>
        public void Save()
        {
            var current = SettingsService.Current;
            current.CopyFrom(Settings);
            current.AutoStartWithWindows = AutoStartWithWindows;
            SettingsService.Save();

            if (AutoStartWithWindows)
            {
                // 无条件重写：用户可能只改了"直接最小化"这一项，
                // 而它体现在注册表命令行的参数上，不重写就不会生效。
                if (!AutoStartRegistration.Enable(minimizedOnStart: Settings.AutoStartMinimized))
                {
                    TsDialog.Show(
                        "开机自启设置失败：\n\n" + AutoStartRegistration.LastError, "提示",
                        TsDialogButtons.Ok, TsDialogIcon.Warning);
                }
            }
            else
            {
                AutoStartRegistration.Disable();
            }
        }

        /// <summary>点「取消」：什么都不做，副本丢弃即可</summary>
        public void Cancel() { }
    }
}
