using System;

namespace TaskScheduler.Triggers
{
    /// <summary>触发器统一接口</summary>
    public interface ITrigger
    {
        TriggerType Type { get; }

        /// <summary>下一次触发时间（事件型触发器返回 null）</summary>
        DateTime? NextRunTime { get; }

        event EventHandler<TriggerFiredEventArgs> Fired;

        void Start();
        void Stop();
        string Describe();
    }

    /// <summary>触发器被触发时携带的上下文信息</summary>
    public class TriggerFiredEventArgs : EventArgs
    {
        public string Message { get; set; }
    }
}
