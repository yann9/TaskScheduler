using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TaskScheduler.Actions;
using TaskScheduler.Triggers;
using TaskScheduler.ViewModels.Sections;

namespace TaskScheduler.ViewModels
{
    /// <summary>
    /// 任务编辑器的外壳。
    ///
    /// 它只做三件事：持有任务草稿、管理四个分区的导航、在确定/取消时统一收尾。
    /// 刻意保持很薄 —— 任何具体字段都应该落在对应的 SectionViewModel 里，
    /// 否则又会回到"改一个字段要在 600 行的 ViewModel 里找半天"的老路。
    ///
    /// 关于草稿：编辑器拿到的必须是 AutomationTask 的副本（见 MainViewModel.EditTask）。
    /// 编辑器里所有绑定都是直接写模型的，若是引擎正在持有的实例，用户点「取消」也只是关窗口，
    /// 改动早已落在对象上，之后引擎任意一次 Save() 都会把它持久化。
    /// </summary>
    public partial class TaskEditorViewModel : ObservableObject
    {
        public AutomationTask Task { get; }

        public TriggerSectionViewModel TriggerSection { get; }
        public ConditionSectionViewModel ConditionSection { get; }
        public ActionSectionViewModel ActionSection { get; }
        public AdvancedSectionViewModel AdvancedSection { get; }

        /// <summary>左侧导航的数据源（顺序即显示顺序）</summary>
        public ObservableCollection<SectionViewModelBase> Sections { get; }

        [ObservableProperty] private SectionViewModelBase _selectedSection;

        /// <summary>顶部标题：新建 / 编辑</summary>
        public string HeaderText => _isNew ? "新建任务" : "编辑任务";

        private readonly bool _isNew;

        /// <summary>新建任务</summary>
        public TaskEditorViewModel() : this(null) { }

        /// <summary>编辑已有任务的副本</summary>
        public TaskEditorViewModel(AutomationTask existing)
        {
            _isNew = existing == null;

            Task = existing ?? new AutomationTask
            {
                Trigger = new TimeTrigger(),
                Action = new RunProgramAction()
            };

            // 分区之间不直接互相依赖；唯一的中转是 Host（见 AdvancedSectionViewModel 的注释）。
            // TriggerSection 必须先建：AdvancedSection 会订阅它的属性变化。
            TriggerSection = new TriggerSectionViewModel(this);
            ConditionSection = new ConditionSectionViewModel(this);
            ActionSection = new ActionSectionViewModel(this);
            AdvancedSection = new AdvancedSectionViewModel(this);

            Sections = new ObservableCollection<SectionViewModelBase>
            {
                TriggerSection, ConditionSection, ActionSection, AdvancedSection
            };

            _selectedSection = TriggerSection; // 默认落在最常改的那一页
            TriggerSection.OnActivated();
        }

        partial void OnSelectedSectionChanged(SectionViewModelBase value)
        {
            value?.OnActivated();
            // 文本框是直接写模型的，ViewModel 收不到通知，只能在这些时机统一刷导航摘要
            NotifyStructureChanged();
        }

        /// <summary>
        /// 任务结构（触发器 / 动作 / 条件）发生了变化 → 刷新各分区的导航摘要。
        /// 由改动方主动调用，父层不需要知道是谁改的。
        /// </summary>
        public void NotifyStructureChanged()
        {
            foreach (var s in Sections) s.Refresh();
            Task.RefreshView(); // 顶部的"下次运行"等展示字段也要跟着变
        }

        /// <summary>点「确定」时调用：让每个分区把界面上残留的状态写回模型</summary>
        public void CommitAll()
        {
            foreach (var s in Sections) s.Commit();
        }

        /// <summary>
        /// 保存前调用：丢弃存档里的"下次执行时间"，让引擎按当前设置重新排期。
        /// 否则改了间隔 / 方式后触发器会沿用旧计划（例如"每天"改成"每隔 30 分钟"还等到明天 9 点才生效）。
        /// HasScheduled 不动，所以不会把已排期过的任务当成新任务、再触发一次"首次执行"。
        /// </summary>
        public void ResetSchedule()
        {
            if (Task.Trigger is TimeTrigger tt) tt.NextRunTime = null;
        }

        /// <summary>窗口关闭时必须调用：释放分区里挂着的静态事件订阅</summary>
        public void Detach() => AdvancedSection.Dispose();
    }
}
