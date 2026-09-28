using System;
using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;
using TaskScheduler.Engine;

namespace TaskScheduler.Service
{
    /// <summary>
    /// 服务安装 / 卸载 / 启停 / 查询。
    /// 创建与删除走 sc.exe（需管理员）；启停与查询走 ServiceController。
    ///
    /// 每个方法都返回 bool 并把细节留在 <see cref="LastMessage"/>：
    /// 调用方是 WinExe（没有控制台），只能靠返回值 + 日志 + 进程退出码把结果传出去，
    /// 否则安装失败时用户看到的就是"点了没反应"。
    /// </summary>
    public static class ServiceControl
    {
        public const string ServiceName = "TaskSchedulerSvc";
        public const string DisplayName = "自动化任务调度服务";

        /// <summary>最近一次操作的说明，供调用方报告给用户</summary>
        public static string LastMessage { get; private set; } = "";

        private static string ExePath
        {
            get
            {
                try { return Process.GetCurrentProcess().MainModule?.FileName ?? ""; }
                catch { return ""; }
            }
        }

        /// <summary>服务应有的命令行：exe 路径带引号 + --service（两者缺一不可）</summary>
        private static string ExpectedBinPath
        {
            get
            {
                var exe = ExePath;
                return string.IsNullOrEmpty(exe) ? "" : "\"" + exe + "\" --service";
            }
        }

        public static bool Install()
        {
            LastMessage = "";

            if (!IsAdministrator())
                return Fail("当前进程没有管理员权限，无法注册系统服务。请以管理员身份运行本程序，"
                            + "或通过安装程序并勾选「安装为后台服务」。");

            var expected = ExpectedBinPath;
            if (string.IsNullOrWhiteSpace(expected))
                return Fail("无法确定程序所在路径（MainModule 读取失败）");

            // 已经装过：命令行一致就只启动；不一致（程序被挪到别的目录 / 旧版注册值被截断）就重建。
            if (QueryStatus() != null)
            {
                var registered = ReadImagePath();
                if (SameCommandLine(registered, expected))
                {
                    if (!StartSvc()) return false;
                    LastMessage = "后台服务此前已安装，已启动（当前状态：" + QueryStatus() + "）";
                    return true;
                }

                Log.Write("后台服务已存在但注册的命令行不一致，将删除后重建。已注册：" + registered);
                if (!StopAndDelete()) return false;
            }

            // create 的 binPath 值必须整体作为一个参数传下去 —— 这是"安装服务永远失败"的真凶：
            // 安装目录在 C:\Program Files\ 下时含空格，值内部的双引号若不转义，
            // CreateProcess 之后命令行会按空格被切开，sc.exe 收到的是
            // binPath=C:\Program 外加一个游离的 Files\...\TaskScheduler.exe 参数，直接报参数错误。
            var args = "create " + ServiceName
                     + " binPath= " + EscapeArg(expected)
                     + " start= auto"
                     + " DisplayName= " + EscapeArg(DisplayName);

            if (!RunSc(args, out var createOut))
                return Fail("创建服务失败（sc create 未成功）。" + Detail(createOut));

            RunSc("description " + ServiceName + " "
                  + EscapeArg("Windows 自动化任务调度后台服务：触发器/条件驱动的任务执行"), out _);

            // 校验真的按期望写进去了。sc create 报成功、ImagePath 却被截断是很隐蔽的失败，
            // 要等到服务启动时才以"找不到文件"的形式爆出来，那时排查成本高得多。
            var written = ReadImagePath();
            if (!string.IsNullOrEmpty(written) && !SameCommandLine(written, expected))
                return Fail("服务已创建，但注册的命令行不正确。\n注册值：" + written + "\n期望值：" + expected);

            if (!StartSvc()) return Fail("服务已创建，但启动失败（详见上方原因）");

            LastMessage = "后台服务已安装并启动";
            return true;
        }

        public static bool Uninstall()
        {
            LastMessage = "";

            if (QueryStatus() == null)
            {
                LastMessage = "后台服务未安装，无需卸载";
                return true;
            }

            if (!IsAdministrator())
                return Fail("卸载服务需要管理员权限，请以管理员身份运行本程序。");

            if (!StopAndDelete()) return false;

            LastMessage = "后台服务已卸载";
            return true;
        }

        /// <summary>停止并删除服务，等到它真的从 SCM 消失</summary>
        private static bool StopAndDelete()
        {
            StopSvc();
            // sc delete 之前必须等服务真的停下来：处于 stopping 状态时删除会报
            // “服务正在停止”，进而被标记为 delete-pending，后续同名 create 要等很久。
            WaitStopped(30000);

            if (!RunSc("delete " + ServiceName, out var delOut))
                return Fail("删除服务失败（可能是服务仍在停止中，稍后重试）。" + Detail(delOut));

            // delete 之后 SCM 里会有一段 "marked for deletion"：此时 create 同名服务会失败
            for (int i = 0; i < 20 && QueryStatus() != null; i++) Thread.Sleep(500);
            return true;
        }

        /// <summary>等待服务进入 Stopped（或消失）</summary>
        private static void WaitStopped(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var st = QueryStatus();
                if (st == null || st == ServiceControllerStatus.Stopped) return;
                Thread.Sleep(300);
            }
        }

        public static bool StartSvc()
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                if (sc.Status != ServiceControllerStatus.Running)
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
                LastMessage = "服务已启动";
                return true;
            }
            catch (InvalidOperationException ex)
            {
                return Fail("启动服务失败：服务不存在或无法访问（需管理员权限）。" + ex.Message);
            }
            catch (System.TimeoutException)
            {
                // 1053：服务没能在超时时间内向 SCM 报告"正在运行"。
                // 这里给出下一步该看哪里，而不是只丢一句"启动失败"。
                return Fail("启动服务超时：服务进程没有在规定时间内响应服务管理器（错误 1053）。\n"
                            + "请检查日志 " + Paths.LogFilePath + " 里服务启动阶段的报错。");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return Fail("启动服务失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                return Fail("启动服务失败：" + ex.Message);
            }
        }

        public static bool StopSvc()
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                if (sc.Status == ServiceControllerStatus.Running ||
                    sc.Status == ServiceControllerStatus.StartPending)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
                LastMessage = "服务已停止";
                return true;
            }
            catch (Exception ex)
            {
                return Fail("停止服务失败：" + ex.Message);
            }
        }

        public static ServiceControllerStatus? QueryStatus()
        {
            try { using var sc = new ServiceController(ServiceName); return sc.Status; }
            catch { return null; }
        }

        /// <summary>
        /// 服务已注册，但注册的命令行与本程序现在的路径对不上
        /// （程序被挪过目录、或旧版热更残留）。
        ///
        /// 为什么界面需要知道这个：正常情况"已安装"时应该把「安装服务」置灰，
        /// 但恰好是"装过、又挪了地方"这种状态最需要重新注册一次 ——
        /// 不给入口的话，用户只能去命令行 sc delete 才能修好。
        /// </summary>
        public static bool RegisteredForDifferentPath()
        {
            var registered = ReadImagePath();
            if (string.IsNullOrWhiteSpace(registered)) return false;   // 注册表里没有 = 没装

            var expected = ExpectedBinPath;
            if (string.IsNullOrWhiteSpace(expected)) return false;      // 读不到自己的路径就不下判断

            return !SameCommandLine(registered, expected);
        }

        /// <summary>读注册表里实际写入的服务命令行（ImagePath）</summary>
        private static string ReadImagePath()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + ServiceName, false);
                return k?.GetValue("ImagePath") as string;
            }
            catch { return null; }
        }

        /// <summary>
        /// 比较两段命令行是否等价。
        /// 宽松处理：忽视引号与多余空格 —— SCM 对未加引号的 ImagePath 有"从长到短切分找存在的文件"
        /// 的回退机制，所以两种写法都能正常启动，没必要为此重建服务。
        /// </summary>
        private static bool SameCommandLine(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);

            static string Normalize(string s)
            {
                s = s.Replace("\"", " ");
                while (s.Contains("  ")) s = s.Replace("  ", " ");
                return s.Trim();
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return true; // 判断不了就不拦，让 sc.exe 自己报错
            }
        }

        private static bool Fail(string message)
        {
            // 把底层（sc.exe / ServiceController）的原始输出一并带上，否则用户只看到
            // "创建服务失败"，排查时还得自己去翻日志。
            var prev = LastMessage;
            LastMessage = string.IsNullOrWhiteSpace(prev) || prev == message
                ? message
                : message + "\n" + prev;
            Log.Error(LastMessage);
            return false;
        }

        private static string Detail(string output)
            => string.IsNullOrWhiteSpace(output) ? "" : "\n\nsc 输出：\n" + output.Trim();

        /// <summary>
        /// 把一个值包成命令行里的单个参数。
        /// 内部双引号写成 \" —— Windows 的命令行解析只认这种形式；不转义就会被空格切开，
        /// 这正是 sc create 在含空格的安装路径下必然失败的原因。
        /// </summary>
        private static string EscapeArg(string value)
            => "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";

        /// <summary>执行 sc 命令，返回是否成功（退出码 0），原始输出经 output 带回</summary>
        private static bool RunSc(string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo("sc.exe", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) { output = "无法启动 sc.exe"; return false; }

                // 必须先异步把输出读走：sc.exe 的输出一旦写满管道缓冲区，
                // 它会阻塞在写、这边阻塞在 WaitForExit → 死锁。
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(30000))
                {
                    try { p.Kill(); } catch { }
                    output = "sc " + args + " 超时未返回";
                    Log.Error(output);
                    return false;
                }

                output = ((so.Result ?? "") + (se.Result ?? "")).Trim();
                Log.Write("sc " + args + " -> [exit " + p.ExitCode + "] " + output);
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                output = "执行 sc 失败：" + ex.Message;
                Log.Error(output);
                return false;
            }
        }
    }
}
