using System;
using System.Diagnostics;

namespace TaskScheduler.Conditions
{
    /// <summary>进程条件：要求某进程“正在运行”或“未运行”</summary>
    public class ProcessRunningCondition : ConditionBase
    {
        public override ConditionType Type => ConditionType.ProcessRunning;

        /// <summary>进程名，可带或不带 .exe</summary>
        private string _processName = "";
        public string ProcessName
        {
            get => _processName;
            set => SetField(ref _processName, value);
        }

        /// <summary>true=要求正在运行；false=要求未运行</summary>
        private bool _mustBeRunning = true;
        public bool MustBeRunning
        {
            get => _mustBeRunning;
            set => SetField(ref _mustBeRunning, value);
        }

        protected override bool EvaluateCore()
        {
            if (string.IsNullOrWhiteSpace(ProcessName)) return true;
            var name = ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? ProcessName.Substring(0, ProcessName.Length - 4)
                : ProcessName;

            // Process.GetProcessesByName 返回的 Process 对象持有本机进程句柄，
            // 用完必须 Dispose —— 条件求值频率不低（条件等待窗口里每几秒一次），
            // 不释放会一直累积句柄。
            bool running = false;
            var procs = Process.GetProcessesByName(name);
            try
            {
                running = procs.Length > 0;
            }
            finally
            {
                foreach (var p in procs)
                {
                    try { p.Dispose(); } catch { }
                }
            }

            return MustBeRunning ? running : !running;
        }

        protected override string DescribeCore
            => (MustBeRunning ? "进程运行中: " : "进程未运行: ") + ProcessName;
    }
}
