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

        public AboutViewModel()
        {
            Version = ResolveVersion();
            OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        }

        /// <summary>优先取 InformationalVersion（CI 可注入），否则退回 AssemblyVersion</summary>
        private static string ResolveVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info;
            var v = asm.GetName().Version;
            return v == null ? "1.0.0" : v.ToString();
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
