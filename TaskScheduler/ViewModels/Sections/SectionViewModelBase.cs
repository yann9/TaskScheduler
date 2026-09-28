using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TaskScheduler.ViewModels.Sections
{
    /// <summary>
    /// 任务编辑器里一个分区的基类。
    ///
    /// 存在的意义就是**隔离**：每个分区的字段、命令、派生属性只出现在自己的子类和自己的 XAML 里。
    /// 改「触发器」页不会碰到「条件」页的绑定，也不会因为某个中间对象为 null
    /// 就把另一片控件带成"默认可见"。原来 950 行单页 XAML 最难维护的正是这一点 ——
    /// 所有分区共用一份 DataContext、一堆 EnumVisibility 转换器，改一处崩别处。
    /// </summary>
    public abstract class SectionViewModelBase : ObservableObject
    {
        protected SectionViewModelBase(TaskEditorViewModel host) { Host = host; }

        /// <summary>所属编辑器。分区之间需要协作时（例如类型切换）统一通过它中转。</summary>
        public TaskEditorViewModel Host { get; }

        public AutomationTask Task => Host.Task;

        /// <summary>左侧导航显示的名字</summary>
        public abstract string Title { get; }

        /// <summary>左侧导航显示的摘要，例如"每天 09:00"、"3 个条件"</summary>
        public abstract string Summary { get; }

        /// <summary>第一次切到该分区时调用，用于初始化只在界面上存在的状态（如本机 IP 快照）</summary>
        public virtual void OnActivated() { }

        /// <summary>数据可能变了：刷新导航摘要等派生属性</summary>
        public virtual void Refresh() => OnPropertyChanged(nameof(Summary));

        /// <summary>点「确定」时调用，把界面上残留的状态写回模型（如星期勾选）</summary>
        public virtual void Commit() { }
    }
}
