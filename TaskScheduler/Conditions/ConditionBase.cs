using System.Runtime.CompilerServices;

namespace TaskScheduler.Conditions
{
    /// <summary>
    /// 条件公共基类：统一处理「取反」与「求值异常兜底」。
    ///
    /// 为什么要有基类：
    /// - 取反若散落在每个条件里各写一遍（return Negate ? !x : x），很容易漏掉某一处，
    ///   而且描述文案也没法统一包一层；放在基类里子类只需要实现 EvaluateCore。
    /// - 条件求值会碰到磁盘、网络、文件系统，任何一个都可能抛异常。
    ///   抛出会被引擎当作"执行失败"而不是"条件不满足"，语义是错的，
    ///   所以统一在基类兜住，异常一律视为条件不成立（取反也不翻转）。
    /// - 属性通知也收在这里：条件的字段被改后，界面上下两处都要跟上 ——
    ///   下面的编辑框（绑字段本身）和上面的条件列表（绑 <see cref="Describe"/>）。
    ///   子类只要用 SetField 赋值，这两处就都活了。
    /// </summary>
    public abstract class ConditionBase : ObservableModel, ICondition
    {
        public abstract ConditionType Type { get; }

        private bool _negate;
        public bool Negate
        {
            get => _negate;
            set => SetField(ref _negate, value);
        }

        /// <summary>子类实现：条件本身的判定逻辑（不含取反）</summary>
        protected abstract bool EvaluateCore();

        public bool Evaluate()
        {
            try
            {
                var ok = EvaluateCore();
                return Negate ? !ok : ok;
            }
            catch
            {
                // 读不到状态（权限、设备被拔、路径非法…）时按"不满足"处理，绝不向上抛：
                // 抛出去引擎会记成"执行失败"，而实际只是条件没达成，还得白跑一次动作。
                return false;
            }
        }

        /// <summary>子类实现：不含取反前缀的描述文案</summary>
        protected abstract string DescribeCore { get; }

        public string Describe => Negate ? "非（" + DescribeCore + "）" : DescribeCore;

        /// <summary>
        /// 字段赋值 + 通知。除通知字段本身外，还要通知 <see cref="Describe"/>：
        /// 它是列表里显示的那一行，由各字段拼出来 —— 不连带通知的话，
        /// 改完条件之后上面列表还挂着旧文案，用户会以为改动没生效。
        /// 条件类的属性一律用这个赋值，不要直接 Set。
        /// </summary>
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string member = null)
        {
            if (!Set(ref field, value, member)) return false;
            OnChanged(nameof(Describe));
            return true;
        }
    }
}
