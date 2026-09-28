using System;
using System.ServiceProcess;
using System.Windows;
using TaskScheduler.Engine;
using TaskScheduler.Service;

namespace TaskScheduler
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // 命令行模式（带了任何参数）下一律不弹模态对话框。
            //
            // 这类调用来自安装器 / 发布脚本：它们要么用"等待进程结束"的方式等本进程
            // （安装器的 Exec），要么在无人值守环境里跑。一个模态框会把调用方**无限期挂住** ——
            // 安装程序卡死就是这个机制。命令行模式下的问题一律写日志 + 用退出码传递。
            var commandLineMode = args != null && args.Length > 0;

            // 早期异常捕获：避免“双击没反应/闪退”却无任何提示
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                try { Log.Init(Paths.LogFilePath); Log.Error("致命异常(域): " + ex); } catch { }
                if (commandLineMode) return;
                try
                {
                    System.Windows.MessageBox.Show(
                        "程序启动失败：\n" + (ex?.ToString() ?? "未知错误"),
                        "自动化任务调度器", System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                }
                catch { }
            };

            try
            {
                if (args.Length > 0)
                {
                    var cmd = args[0].ToLowerInvariant();

                    if (cmd == "--service")
                    {
                        ServiceBase.Run(new AutomationService());
                        return;
                    }

                    // 请求已运行的实例优雅退出（走完 OnExit 的 Stop()+Save()，把最新排期落盘）。
                    // 安装器覆盖文件前、发布脚本重新发布前都靠它 —— 直接用 taskkill 或
                    // CloseMainWindow 都不行：CloseAction 默认是"最小化到托盘"，
                    // 关闭窗口会被拦下（e.Cancel = true），进程照样活着、文件照样被占用。
                    //
                    // 刻意不初始化日志、也不写 service-result.txt：
                    // 这不是服务操作，覆盖结果文件会让界面显示错误的服务安装结果；
                    // 而本分支只发一个信号，失败也没有可记录的有用信息。
                    if (cmd == "--exit")
                    {
                        // 找不到实例时静默返回：调用方要的语义是"确保现在没有实例在跑"，
                        // 本来就没有实例，就是已经满足条件，不该报失败。
                        App.RequestExistingInstanceExit();
                        return;
                    }

                    // 下面这些分支的结果必须让调用方看得见：
                    // 主程序是 WinExe，Console.WriteLine 没有控制台可去 ——
                    // 所以先初始化日志（否则 Log.Write 只写内存缓冲，等于丢了），
                    // 再用进程退出码把成功/失败传出去（安装器与提权的父进程都靠它判断）。
                    //
                    // 注意 Log.Init 放在确认是服务命令之后再调：非服务类参数（例如自启项带的
                    // --minimized）要落到下面的界面分支，不该在这里重复初始化日志。
                    bool ok = true, known = true;
                    switch (cmd)
                    {
                        case "--install-service": ok = ServiceControl.Install(); break;
                        case "--uninstall-service": ok = ServiceControl.Uninstall(); break;
                        case "--start-service": ok = ServiceControl.StartSvc(); break;
                        case "--stop-service": ok = ServiceControl.StopSvc(); break;
                        default: known = false; break;
                    }

                    if (known)
                    {
                        Log.Init(Paths.LogFilePath);
                        Log.Write($"{cmd} 完成：{(ok ? "成功" : "失败")}｜{ServiceControl.LastMessage}");
                        WriteServiceResult(ok, ServiceControl.LastMessage);
                        Environment.ExitCode = ok ? 0 : 1;
                        return;
                    }
                }

                // 默认：WPF 界面
                var app = new App();
                app.InitializeComponent();
                app.Run();
            }
            catch (Exception ex)
            {
                try { Log.Init(Paths.LogFilePath); Log.Error("Main 异常: " + ex); } catch { }
                if (commandLineMode)
                {
                    // 见 Main 开头 commandLineMode 的说明：自动化调用里绝不能弹框。
                    // 用退出码把失败传出去（安装器与提权的父进程都靠它判断）。
                    Environment.ExitCode = 1;
                    return;
                }
                System.Windows.MessageBox.Show(
                    "程序启动失败：\n" + ex.Message,
                    "自动化任务调度器", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 把服务管理的结果写回传文件，供提权的父进程读取展示。
        /// 父进程拿不到子进程的 stdout（WinExe 无控制台，且提权启动必须 UseShellExecute=true），
        /// 只靠退出码的话，用户在界面上只能看到"失败"两个字，具体原因还得自己去翻日志。
        /// </summary>
        private static void WriteServiceResult(bool ok, string message)
        {
            try
            {
                var path = Paths.ServiceResultFilePath;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, (ok ? "OK" : "FAIL") + "\n" + (message ?? ""));
            }
            catch { }
        }
    }
}
