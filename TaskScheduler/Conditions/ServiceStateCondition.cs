using System;
using System.Linq;
using System.ServiceProcess;

namespace TaskScheduler.Conditions
{
    /// <summary>
    /// 服务状态条件：某个 Windows 服务正在运行（或已停止）时才执行。
    ///
    /// 典型用法：依赖上游套件的任务挂上「服务 xxx 正在运行」，
    /// 上游没起来就直接跳过，而不是让动作失败一堆报错。
    ///
    /// 服务名与显示名都支持：先按服务名找，找不到就在显示名里匹配一次 ——
    /// 界面上「服务」里看到的是显示名（"Print Spooler"），
    /// 而 sc / net 命令用的是服务名（"Spooler"），用户填哪个都可能。
    /// </summary>
    public class ServiceStateCondition : ConditionBase
    {
        public override ConditionType Type => ConditionType.ServiceState;

        private string _serviceName = "";
        public string ServiceName
        {
            get => _serviceName;
            set => SetField(ref _serviceName, value);
        }

        /// <summary>true = 要求正在运行；false = 要求已停止</summary>
        private bool _mustBeRunning = true;
        public bool MustBeRunning
        {
            get => _mustBeRunning;
            set => SetField(ref _mustBeRunning, value);
        }

        protected override bool EvaluateCore()
        {
            var name = (ServiceName ?? "").Trim();
            if (name.Length == 0) return false;

            var status = QueryStatus(name);
            if (status == null) return false;    // 服务不存在 / 查不动 → 不满足

            bool running = status.Value == ServiceControllerStatus.Running ||
                           status.Value == ServiceControllerStatus.StartPending;
            return MustBeRunning ? running : !running;
        }

        private static ServiceControllerStatus? QueryStatus(string name)
        {
            // 1) 按服务名直接查（最快，覆盖绝大多数情况）
            try
            {
                using (var sc = new ServiceController(name))
                {
                    var t = sc.Status;            // 触发一次实际查询，服务不存在时在这里抛
                    return t;
                }
            }
            catch { }

            // 2) 退回到显示名匹配
            try
            {
                foreach (var sc in ServiceController.GetServices())
                {
                    using (sc)
                    {
                        try
                        {
                            if (string.Equals(sc.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                                return sc.Status;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return null;
        }

        protected override string DescribeCore
            => string.IsNullOrWhiteSpace(ServiceName)
                ? "服务状态（未填写服务名）"
                : (MustBeRunning ? "服务运行中：" : "服务已停止：") + ServiceName.Trim();
    }
}
