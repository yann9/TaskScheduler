using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using TaskScheduler.Engine;

namespace TaskScheduler.ViewModels
{
    /// <summary>
    /// 「关于」窗口：展示产品名、版本、简介、版权，并提供"打开数据目录"。
    /// 纯展示，无需通知机制 —— 字段在构造时一次性算好，绑定读取一次即可。
    /// </summary>
    public class AboutViewModel
    {
        public string ProductName { get; } = "自动化任务调度器";
        public string Version { get; }
        public string Copyright { get; } = "© 2026 YannCC";

        public string Description { get; } =
            "Windows 下基于多种触发器 + 条件 + 动作的定时 / 自动化任务桌面程序。" +
            "支持以 Windows 后台服务方式保活运行（无需登录用户）、单实例 + 命名管道 IPC 远程控制，" +
            "以及中国法定节假日判断。";

        public string TechInfo { get; } = "运行环境：.NET Framework 4.8　|　安装包：NSIS";

        public ICommand OpenDataFolderCommand { get; }

        /// <summary>关于窗口里的「检查更新」（与帮助菜单、设置页共用同一个窗口）</summary>
        public ICommand CheckUpdateCommand { get; }

        public AboutViewModel()
        {
            // 与更新检查共用同一个版本号来源，避免"关于显示 1.2.0、检查拿 1.2.0.0 去比"的错位
            Version = AppVersion.Current;
            OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
            CheckUpdateCommand = new RelayCommand(CheckUpdate);
        }

        private void CheckUpdate()
        {
            var win = new Views.UpdateWindow
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            win.ShowDialog();
        }

        private void OpenDataFolder()
        {
            var dir = Paths.DataDir;
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // explorer 打开目录（/select 会顺带选中目录本身）
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dir}\"")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // 打开失败不弹窗打扰：关于窗口本就是"看一眼"的地方
            }
        }
    }
}
