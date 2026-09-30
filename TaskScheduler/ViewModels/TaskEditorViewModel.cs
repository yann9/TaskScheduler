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

        /// <summary>编辑器打开时任务原本的排期签名（见 ResetSchedule 的说明）</summary>
        private readonly string _scheduleSignatureAtOpen;

        /// <summary>编辑器打开时任务原本的首次运行参数签名（见 ResetSchedule 的说明）</summary>
        private readonly string _firstRunSignatureAtOpen;

        /// <summary>新建任务</summary>
        public TaskEditorViewModel() : this(null) { }

        /// <summary>编辑已有任务的副本</summary>
        public TaskEditorViewModel(AutomationTask existing)
        {
            _isNew = existing == null;

            Task = existing ?? new AutomationTask
            {
                Trigger = new TimeTrigger(),
                Action = new RunProgramAction(),
                // 新任务默认禁用（2026-09-30 周工拍板）："保存"与"生效"分开 ——
                // 配置完先落在列表里，点一次「启用」才参与调度，避免排期参数没核对
                // 就开跑。编辑器顶部的「启用此任务」复选框就是这个初始状态，想保存
                // 即跑可以当场勾上。
                Enabled = false
            };

            _scheduleSignatureAtOpen = (Task.Trigger as TimeTrigger)?.ScheduleSignature() ?? "";
            _firstRunSignatureAtOpen = (Task.Trigger as TimeTrigger)?.FirstRunSignature() ?? "";

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
        /// 保存前调用：排期或首次运行参数真的改了，才让引擎按新设置重新排期。
        ///
        /// 分两条路（签名分开比，见 <see cref="TimeTrigger.FirstRunSignature"/>）：
        ///   1) 首次运行参数变了（仅间隔方式）→ 清掉下次执行时间**并把 HasScheduled 复位**，
        ///      让引擎把任务当成"从未排期"重新走一次首次执行策略。只清 NextRunTime 是不够的：
        ///      Start() 会因为 HasScheduled=true 走 ComputeNext（当前时刻 + 间隔），
        ///      新的首次参数依然被忽略 —— 复制任务改首次参数不生效就是这条路漏了（踩过）。
        ///   2) 常规排期变了 → 只清下次执行时间，从当前时刻按新参数重排；
        ///      HasScheduled 保持不动，避免把已排期过的任务再触发一次"立即执行"。
        ///      间隔任务的计时锚点会变成"编辑时刻"，这正是改间隔的用户预期的行为。
        /// 只改动作 / 条件 / 备注时两个签名都不变，什么都不动 —— 原计划保留。
        /// </summary>
        public void ResetSchedule()
        {
            var tt = Task.Trigger as TimeTrigger;
            if (tt == null) return;

            if (tt.FirstRunSignature() != _firstRunSignatureAtOpen)
            {
                tt.NextRunTime = null;
                tt.HasScheduled = false;
                return;
            }

            if (tt.ScheduleSignature() == _scheduleSignatureAtOpen) return;
            tt.NextRunTime = null;
        }

        /// <summary>窗口关闭时必须调用：释放分区里挂着的静态事件订阅</summary>
        public void Detach() => AdvancedSection.Dispose();
    }
}
