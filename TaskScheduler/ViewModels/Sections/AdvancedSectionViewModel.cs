using System;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskScheduler.Triggers;

namespace TaskScheduler.ViewModels.Sections
{
    /// <summary>
    /// 「高级」分区：排期微调（随机抖动、跳过规则、节假日数据）与任务备注。
    ///
    /// 这些设置的特点都是"平时不动、出问题时才来改"，所以归在一起，
    /// 让「触发器」页保持只有"什么时候跑"这一件事。
    /// </summary>
    public partial class AdvancedSectionViewModel : SectionViewModelBase, IDisposable
    {
        public AdvancedSectionViewModel(TaskEditorViewModel host) : base(host)
        {
            // 抖动 / 跳过规则都只对"时间触发"有意义，而可见性取决于触发器分区的选择，
            // 所以订阅它的变化（这是两个分区之间唯一的耦合点，明确写在这里，而不是靠隐式绑定）。
            Host.TriggerSection.PropertyChanged += OnTriggerSectionChanged;
            Engine.HolidayProvider.StatusChanged += OnHolidayStatusChanged;
        }

        public override string Title => "高级";

        public override string Summary
        {
            get
            {
                if (Task.Trigger is not TimeTrigger) return "—";

                var parts = new System.Collections.Generic.List<string>();
                if (Task.Trigger is TimeTrigger tt)
                {
                    if (tt.RandomJitterMinutes > 0) parts.Add("抖动 " + tt.RandomJitterMinutes + " 分钟");
                    if (tt.SkipWeekend) parts.Add("跳过周末");
                    if (tt.SkipHolidays) parts.Add("跳过节假日");
                    if (!string.IsNullOrWhiteSpace(tt.ExtraSkipDates)) parts.Add("额外跳过日期");
                }
                return parts.Count == 0 ? "默认" : string.Join("；", parts);
            }
        }

        /// <summary>当前触发器的强类型引用（非时间触发器时为 null，整块内容随之隐藏）</summary>
        public TimeTrigger TimeTriggerObj => Task.Trigger as TimeTrigger;

        /// <summary>抖动与跳过规则是否适用（只对时间触发器有意义）</summary>
        public bool IsTimeTrigger => Task.Trigger is TimeTrigger;

        /// <summary>
        /// 是否显示"跳过周末 / 节假日"这组设置。
        /// 只对 每天 / 每周 / 每月 / 一次性 有意义 —— 间隔触发是纯粹的时间累加，
        /// 没有"哪一天"的概念，勾了也不会生效，所以干脆对间隔方式隐藏。
        /// （用布尔属性而不是 EnumVis：要排除的是"间隔"这一个值，其余情况全显示，
        ///   而 EnumVis 只能做等值匹配。）
        /// </summary>
        public bool ShowSkipRules
            => Host.TriggerSection.SelectedTriggerType == TriggerType.Time
               && Host.TriggerSection.TriggerKind != TimeTriggerKind.Interval;

        /// <summary>
        /// 节假日数据的覆盖范围说明（随同步结果动态变化，所以订阅了 HolidayProvider.StatusChanged）。
        /// 必须显示在界面上：国务院一般前一年 11 月才公布次年安排，
        /// 数据源里没有的年份只会按"周末"判断 —— 用户以为勾了"跳过法定节假日"就万事大吉、
        /// 结果春节照跑，那才是坑。
        /// </summary>
        public string HolidayCoverageText => ChineseCalendar.CoverageText;

        /// <summary>节假日同步状态文案（"已同步 3 个年度（更新于 09-23 22:30）" / "同步失败…"）</summary>
        public string HolidaySyncText => Engine.HolidayProvider.Status.Message;

        /// <summary>没有正在刷新时才允许点「立即刷新」</summary>
        public bool IsHolidayRefreshIdle => !Engine.HolidayProvider.Status.IsRefreshing;

        public ICommand RefreshHolidaysCommand { get; } = new RelayCommand(() =>
        {
            // 后台跑，别把界面卡住。刷新完成后由 StatusChanged 事件驱动界面更新。
            System.Threading.Tasks.Task.Run(async () =>
            {
                try { await Engine.HolidayProvider.RefreshAsync(true); }
                catch (Exception ex) { Engine.Log.Write("手动刷新节假日失败：" + ex.Message, "WARN"); }
            });
        });

        /// <summary>下次计划执行时间（只读预览，实际排期由引擎在保存后重算）</summary>
        public string NextRunPreview => Task.NextRunText;

        public override void OnActivated()
        {
            OnPropertyChanged(nameof(NextRunPreview));
            OnPropertyChanged(nameof(IsTimeTrigger));
            OnPropertyChanged(nameof(TimeTriggerObj));
            OnPropertyChanged(nameof(ShowSkipRules));
        }

        public override void Refresh()
        {
            base.Refresh();
            OnPropertyChanged(nameof(NextRunPreview));
        }

        private void OnTriggerSectionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TriggerSectionViewModel.TriggerKind)
                || e.PropertyName == nameof(TriggerSectionViewModel.SelectedTriggerType))
            {
                OnPropertyChanged(nameof(ShowSkipRules));
                OnPropertyChanged(nameof(IsTimeTrigger));
                OnPropertyChanged(nameof(TimeTriggerObj));
            }
        }

        /// <summary>节假日同步跑在后台线程，事件到达时要切回 UI 线程再动绑定属性</summary>
        private void OnHolidayStatusChanged(object sender, EventArgs e)
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d == null) return;
            if (d.CheckAccess()) RaiseHolidayProps();
            else d.BeginInvoke(new Action(RaiseHolidayProps));
        }

        private void RaiseHolidayProps()
        {
            OnPropertyChanged(nameof(HolidayCoverageText));
            OnPropertyChanged(nameof(HolidaySyncText));
            OnPropertyChanged(nameof(IsHolidayRefreshIdle));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>
        /// 解绑全局事件。窗口关闭时必须调用 —— HolidayProvider.StatusChanged 是静态事件，
        /// 不解绑会一直持有 ViewModel，每开一次编辑器就泄漏一个。
        /// </summary>
        public void Dispose()
        {
            Host.TriggerSection.PropertyChanged -= OnTriggerSectionChanged;
            Engine.HolidayProvider.StatusChanged -= OnHolidayStatusChanged;
        }
    }
}
