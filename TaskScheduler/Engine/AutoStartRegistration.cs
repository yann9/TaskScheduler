using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace TaskScheduler.Engine
{
    /// <summary>
    /// 登录自启（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）。
    ///
    /// 两个要点：
    /// 1) 值名必须与安装器（TaskScheduler.nsi）完全一致。两边用不同名字时，
    ///    登录会被拉起两个实例 —— 本地托管模式下就是两个引擎跑同一批任务、互相覆盖排期。
    /// 2) 用 HKCU 而不是 HKLM：普通用户即可写；"哪个用户登录"本来就是用户级语义。
    /// </summary>
    public static class AutoStartRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>当前使用的值名（与安装器保持一致）</summary>
        public const string ValueName = "TaskScheduler";

        /// <summary>
        /// 自启命令行里的"启动后直接最小化到托盘"开关。
        ///
        /// 为什么用命令行参数而不是读设置文件：注册表和设置文件是两份状态，
        /// 靠读设置文件无法区分"登录自启"和"用户手动双击"——两者命令行完全一样。
        /// 早期版本就是这么干的，结果是设了这项之后手动双击图标也什么都看不到。
        /// </summary>
        public const string MinimizedArgument = "--minimized";

        /// <summary>最近一次操作的失败原因（成功时为空字符串）</summary>
        public static string LastError { get; private set; } = "";

        /// <summary>当前进程的 exe 完整路径；取不到时返回空串</summary>
        public static string CurrentExePath
        {
            get
            {
                try { return Process.GetCurrentProcess().MainModule?.FileName ?? ""; }
                catch { return ""; }
            }
        }

        /// <summary>是否已设置自启</summary>
        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                if (key == null) return false;
                return key.GetValue(ValueName) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 自启命令里是否带了 <see cref="MinimizedArgument"/>（登录后直接进托盘，不弹主窗口）。
        /// 设置窗口用它显示实况 —— 用户可能在安装器里勾选、也可能直接改注册表，
        /// 光看设置文件里的字段会与实际不符。
        /// </summary>
        public static bool IsMinimizedOnStart()
        {
            var cmd = ReadCommandLine();
            return cmd.Length > 0
                && cmd.IndexOf(MinimizedArgument, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>读自启命令行原文；没有则返回空串</summary>
        private static string ReadCommandLine()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                if (key == null) return "";
                return key.GetValue(ValueName) as string ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 设置自启。exePath 留空表示当前进程的 exe；
        /// minimizedOnStart 为 true 时在命令行尾部追加 <see cref="MinimizedArgument"/>。
        /// </summary>
        public static bool Enable(string exePath = null, bool minimizedOnStart = false)
        {
            LastError = "";
            try
            {
                var exe = string.IsNullOrEmpty(exePath) ? CurrentExePath : exePath;
                if (string.IsNullOrEmpty(exe))
                {
                    LastError = "无法确定程序路径";
                    return false;
                }

                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key == null)
                {
                    LastError = @"无法打开注册表自启项（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）";
                    return false;
                }

                // 路径必须带引号：安装目录含空格时（C:\Program Files\...），
                // 不带引号的命令行会被 Windows 按空格切开，登录时启动失败。
                var cmd = "\"" + exe + "\"";
                if (minimizedOnStart) cmd += " " + MinimizedArgument;

                key.SetValue(ValueName, cmd);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public static bool Disable()
        {
            LastError = "";
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key == null)
                {
                    LastError = @"无法打开注册表自启项（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）";
                    return false;
                }

                key.DeleteValue(ValueName, false);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
    }
}
