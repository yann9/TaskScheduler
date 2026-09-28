using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using TaskScheduler.Engine;

namespace TaskScheduler.Actions
{
    /// <summary>动作统一接口</summary>
    public interface IAction
    {
        ActionType Type { get; }
        Task ExecuteAsync(ActionContext context, CancellationToken ct);
        string Describe();
    }

    /// <summary>动作执行上下文</summary>
    public class ActionContext
    {
        public INotificationService Notifier { get; set; } = NullNotificationService.Instance;
        public Dispatcher Dispatcher { get; set; }
        public string TriggerMessage { get; set; }

        /// <summary>
        /// 动作输出的汇集处（可空）。引擎最终把它写进运行记录 ——
        /// 以前脚本的 stdout 只进日志文件，界面上只知道"成功 / 失败"，
        /// 想看看到底打印了什么还得自己去翻 scheduler.log。
        /// </summary>
        public System.Text.StringBuilder Output { get; set; }

        /// <summary>往运行记录里追加一段输出（未提供 Output 时静默忽略）</summary>
        public void AppendOutput(string text)
        {
            var sb = Output;
            if (sb == null || string.IsNullOrEmpty(text)) return;
            lock (sb) sb.AppendLine(text);
        }
    }
}
