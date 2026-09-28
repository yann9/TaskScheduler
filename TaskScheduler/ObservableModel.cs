using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TaskScheduler
{
    /// <summary>
    /// 可观察模型基类：属性变化时通知 WPF 刷新绑定。
    ///
    /// 为什么动作 / 条件这些"纯数据"也必须有通知：
    /// 编辑器里的文本框是直接绑到模型属性上的（<c>RunActionObj.Program</c> 这种路径）。
    /// 只要是**代码**去写这些属性（点「浏览…」选了文件、勾选联动、程序化填充默认值），
    /// 没有通知的话界面根本不会重读 —— 表现就是"选了文件，框里还是空的"，
    /// 而且不报任何错，排查起来只能靠猜。
    ///
    /// 和 <see cref="AutomationTask"/> 保持同一套手写风格（不引 MVVM 框架的属性生成器）：
    /// 模型层要保持可序列化、可跨进程传输，依赖越少越好。
    /// </summary>
    public abstract class ObservableModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnChanged([CallerMemberName] string member = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(member));

        /// <summary>值真的变了才写回并通知；返回是否发生了变化</summary>
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string member = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnChanged(member);
            return true;
        }
    }
}
