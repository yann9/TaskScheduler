using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TaskScheduler.Engine;

namespace TaskScheduler.Actions
{
    /// <summary>运行程序 / 脚本</summary>
    public class RunProgramAction : ObservableModel, IAction
    {
        public ActionType Type => ActionType.RunProgram;

        // 属性一律用 Set 赋值：编辑器直接绑这些属性，点「浏览…」是代码写值，
        // 不通知的话文本框不会更新（选了文件框里还是空的）。
        private string _program = "";
        public string Program
        {
            get => _program;
            set => Set(ref _program, value);
        }

        private string _arguments = "";
        public string Arguments
        {
            get => _arguments;
            set => Set(ref _arguments, value);
        }

        private string _workingDirectory = "";
        public string WorkingDirectory
        {
            get => _workingDirectory;
            set => Set(ref _workingDirectory, value);
        }

        private bool _waitForExit = true;
        public bool WaitForExit
        {
            get => _waitForExit;
            set => Set(ref _waitForExit, value);
        }

        private int _timeoutSeconds = 60;
        public int TimeoutSeconds
        {
            get => _timeoutSeconds;
            set => Set(ref _timeoutSeconds, value);
        }

        /// <summary>
        /// 退出码非 0 是否算失败。默认算失败 —— 脚本出错却报"执行成功"是最糟的反馈。
        /// 需要"非 0 也算正常"的场景（grep 无匹配返回 1 之类）可以关掉。
        /// </summary>
        private bool _failOnNonZeroExitCode = true;
        public bool FailOnNonZeroExitCode
        {
            get => _failOnNonZeroExitCode;
            set => Set(ref _failOnNonZeroExitCode, value);
        }

        public async Task ExecuteAsync(ActionContext context, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Program))
                throw new InvalidOperationException("未指定程序路径");

            // 等待退出时才重定向输出：不等待的话我们不会去读管道，
            // 重定向反而会让子进程写满缓冲区后永久阻塞。
            bool capture = WaitForExit;

            var psi = new ProcessStartInfo
            {
                FileName = Program,
                Arguments = Arguments,
                // 这三项是必须的：UseShellExecute=true 会弹出一个控制台窗口
                //（跑 bash.exe / .bat 尤其明显，服务模式下则完全看不到），
                // 而且拿不到退出码与输出。
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = capture,
                RedirectStandardError = capture
            };
            if (!string.IsNullOrWhiteSpace(WorkingDirectory))
                psi.WorkingDirectory = WorkingDirectory;

            // bash / .sh 的输出是 UTF-8；不指定编码时 .NET 按系统 ANSI（GBK）解码，
            // 中文日志必乱码。cmd/bat 输出 ANSI，保持默认。
            if (Program.IndexOf("bash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                Program.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
            {
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
            }

            Log.Write($"[动作] 启动：{Program} {Arguments}".TrimEnd());

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("启动进程失败（路径是否存在 / 权限是否足够）");

            if (!capture)
            {
                Log.Write($"[动作] 已启动（不等待退出），PID={p.Id}");
                context.AppendOutput($"已启动（不等待退出），PID={p.Id}");
                return;
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (s, e) => { if (e.Data != null) AppendLimited(stdout, e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) AppendLimited(stderr, e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            var exitTask = Task.Run(() => p.WaitForExit());
            var timeoutMs = Math.Max(0, TimeoutSeconds) * 1000;

            if (timeoutMs > 0)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(timeoutMs, cts.Token);
                if (await Task.WhenAny(exitTask, delay) != exitTask)
                {
                    // 超时必须真的把进程干掉：只抛异常的话脚本会一直挂着，
                    // 线程池线程也一直被 WaitForExit 占着。
                    KillTree(p);
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    throw new TimeoutException($"程序在 {TimeoutSeconds}s 内未退出，已强制结束（PID {p.Id}）");
                }
                cts.Cancel(); // 退出已发生，取消计时器
            }
            else
            {
                await exitTask; // 0 = 不限时
            }

            await exitTask;
            // 有异步输出时，无参 WaitForExit 会等输出回调处理完，避免丢掉最后几行
            await Task.Run(() => p.WaitForExit());

            var outText = OneLine(stdout.ToString());
            var errText = OneLine(stderr.ToString());
            if (outText.Length > 0) Log.Write($"[动作] 输出：{outText}");
            if (errText.Length > 0) Log.Write($"[动作] 错误输出：{errText}");

            // 原始多行输出进运行记录：日志里为了不刷屏压成了一行，
            // 但用户点开某一次运行记录想看的就是完整内容。
            if (stdout.Length > 0) context.AppendOutput("--- 标准输出 ---\r\n" + stdout.ToString().TrimEnd());
            if (stderr.Length > 0) context.AppendOutput("--- 错误输出 ---\r\n" + stderr.ToString().TrimEnd());

            int code;
            try { code = p.ExitCode; }
            catch (Exception) { return; }

            if (FailOnNonZeroExitCode && code != 0)
            {
                var detail = errText.Length > 0 ? "：" + errText : (outText.Length > 0 ? "：" + outText : "");
                throw new InvalidOperationException($"程序返回退出码 {code}{detail}");
            }

            Log.Write($"[动作] 结束：退出码 {code}");
        }

        private const int MaxCapturedChars = 8192;

        private static void AppendLimited(StringBuilder sb, string line)
        {
            lock (sb)
            {
                if (sb.Length >= MaxCapturedChars) return;
                sb.AppendLine(line);
            }
        }

        /// <summary>把多行输出压成一行并截断，避免把日志撑爆</summary>
        private static string OneLine(string s, int max = 400)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length <= max ? s : "…" + s.Substring(s.Length - max);
        }

        /// <summary>
        /// 结束进程及其子进程树。
        /// .NET Framework 的 Process 没有 Kill(entireProcessTree)（那是 .NET Core 3.0+ 的 API），
        /// 只 Kill 主进程的话，脚本拉起的子进程会变成孤儿继续跑，所以这里用 taskkill /T。
        /// </summary>
        private static void KillTree(Process p)
        {
            try
            {
                if (p.HasExited) return;
                using var tk = Process.Start(new ProcessStartInfo("taskkill", $"/PID {p.Id} /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                tk?.WaitForExit(5000);
            }
            catch (Exception ex) { Log.Error("结束进程失败：" + ex.Message); }

            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        public string Describe()
        {
            var s = $"运行程序：{Program}";
            if (!string.IsNullOrWhiteSpace(Arguments)) s += " " + Arguments;
            return s.Trim();
        }
    }
}
