namespace TaskScheduler.Conditions
{
    /// <summary>执行条件统一接口：触发器触发后、动作执行前需全部满足</summary>
    public interface ICondition
    {
        ConditionType Type { get; }

        /// <summary>
        /// 取反：条件本身不成立时才算通过。
        /// 用来表达"没接电源时"、"进程不在跑时"、"IP 不是公司网段时"这类反向条件，
        /// 不必为每种条件再单独做一个"非"版本。
        /// </summary>
        bool Negate { get; set; }

        bool Evaluate();

        /// <summary>
        /// 界面展示用描述。必须声明为“属性”：WPF 绑定表达式只认属性/字段，
        /// 绑到方法上会静默失败（列表项渲染成空白），且不报任何错。
        /// </summary>
        string Describe { get; }
    }
}
