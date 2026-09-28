using TaskScheduler.Native;

namespace TaskScheduler.Conditions
{
    /// <summary>
    /// 系统负载条件：机器不太忙（或内存够用）时才执行。
    ///
    /// 典型用法：把"全盘索引 / 大文件压缩 / 构建"这类吃资源的任务挂上"CPU 不高于 40%"，
    /// 或者给清理任务配"可用内存不足 2GB"，避免和用户正在做的事抢机器。
    ///
    /// CPU 用的是 GetSystemTimes 差值（见 NativeMethods），第一次求值要采样两次、约 200ms；
    /// 之后每次求值都复用上一次采样点，所以算出来的其实是"距上次求值这段时间的平均占用" ——
    /// 这个语义恰好适合做"机器忙不忙"的判断，比瞬时值稳定得多。
    /// </summary>
    public class SystemLoadCondition : ConditionBase
    {
        public override ConditionType Type => ConditionType.SystemLoad;

        private LoadMetric _metric = LoadMetric.CpuPercent;
        public LoadMetric Metric
        {
            get => _metric;
            set => SetField(ref _metric, value);
        }

        /// <summary>阈值：CPU 是百分比（0–100），内存是 MB</summary>
        private int _threshold = 50;
        public int Threshold
        {
            get => _threshold;
            set => SetField(ref _threshold, value);
        }

        /// <summary>
        /// true = 指标【低于】阈值时通过（机器空闲 / 内存紧张）；
        /// false = 指标【高于】阈值时通过。
        /// 默认"低于"：这类条件绝大多数场景都是「不忙才跑」。
        /// </summary>
        private bool _requireBelow = true;
        public bool RequireBelow
        {
            get => _requireBelow;
            set => SetField(ref _requireBelow, value);
        }

        protected override bool EvaluateCore()
        {
            double value;
            if (Metric == LoadMetric.CpuPercent)
            {
                value = NativeMethods.GetCpuUsagePercent();
                if (value < 0) return false;                 // 采样失败 → 不满足，绝不"当作空闲"放行
            }
            else
            {
                value = NativeMethods.GetAvailablePhysicalMemoryMB();
                if (value < 0) return false;
            }

            return RequireBelow ? value <= Threshold : value >= Threshold;
        }

        protected override string DescribeCore
        {
            get
            {
                if (Metric == LoadMetric.CpuPercent)
                    return RequireBelow
                        ? $"CPU 占用不高于 {Threshold}%"
                        : $"CPU 占用不低于 {Threshold}%";

                var t = Threshold >= 1024 ? (Threshold / 1024.0).ToString("0.##") + " GB" : Threshold + " MB";
                return RequireBelow
                    ? $"可用内存不足 {t}"
                    : $"可用内存不少于 {t}";
            }
        }
    }
}
