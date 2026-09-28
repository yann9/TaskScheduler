using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskScheduler.Triggers;

namespace TaskScheduler.ViewModels.Sections
{
    /// <summary>
    /// 「触发器」分区：决定任务什么时候跑。
    /// 只读写 <see cref="AutomationTask.Trigger"/>，与条件、动作没有任何耦合。
    /// </summary>
    public partial class TriggerSectionViewModel : SectionViewModelBase
    {
        public TriggerSectionViewModel(TaskEditorViewModel host) : base(host)
        {
            WeekdayOptions = new ObservableCollection<WeekdayOption>();
            BrowseWatchPathCommand = new RelayCommand(() =>
                SetWatchPath(Pickers.Folder(FileTriggerObj?.Path)));
            SyncFromTask();
            _initializing = false;
        }

        /// <summary>选监听目录（避免手敲路径）</summary>
        public ICommand BrowseWatchPathCommand { get; }

        /// <summary>
        /// 写入「文件监听」的监视目录。浏览命令与自测都走这里，保证只有一条回写路径。
        ///
        /// 触发器模型目前还是纯 POCO（不像动作 / 条件那样接了属性通知），
        /// 代码写值之后必须自己喊一声：通知"父属性"会让 WPF 重新求值整条
        /// FileTriggerObj.Path 绑定，文本框里才会出现刚选的目录 ——
        /// 漏掉这一步的表现就是"选了目录但框里空的"。
        /// 哪天给 Triggers 也接上 INotifyPropertyChanged，这两行通知就可以删掉。
        /// </summary>
        public void SetWatchPath(string path)
        {
            var target = FileTriggerObj;
            if (target == null || string.IsNullOrEmpty(path)) return;

            target.Path = path;
            OnPropertyChanged(nameof(FileTriggerObj));
            OnPropertyChanged(nameof(Summary));
        }

        public override string Title => "触发器";

        public override string Summary
        {
            get
            {
                var d = Task.Trigger?.Describe() ?? "（未配置）";
                return d.Length <= 24 ? d : d.Substring(0, 24) + "…";
            }
        }

        // ===== 下拉框数据源 =====
        public List<TriggerType> TriggerTypes { get; } =
            Enum.GetValues(typeof(TriggerType)).Cast<TriggerType>().ToList();
        public List<TimeTriggerKind> TimeKinds { get; } =
            Enum.GetValues(typeof(TimeTriggerKind)).Cast<TimeTriggerKind>().ToList();
        public List<SystemEventType> SystemEvents { get; } =
            Enum.GetValues(typeof(SystemEventType)).Cast<SystemEventType>().ToList();
        public List<FileWatcherChangeType> FileChanges { get; } =
            Enum.GetValues(typeof(FileWatcherChangeType)).Cast<FileWatcherChangeType>().ToList();
        public List<IntervalUnit> IntervalUnits { get; } =
            Enum.GetValues(typeof(IntervalUnit)).Cast<IntervalUnit>().ToList();
        public List<IntervalFirstRunMode> FirstRunModes { get; } =
            Enum.GetValues(typeof(IntervalFirstRunMode)).Cast<IntervalFirstRunMode>().ToList();
        public List<MonthlyMode> MonthlyModes { get; } =
            Enum.GetValues(typeof(MonthlyMode)).Cast<MonthlyMode>().ToList();
        public List<DayOfWeek> WeekDays { get; } =
            Enum.GetValues(typeof(DayOfWeek)).Cast<DayOfWeek>().ToList();

        /// <summary>"每月第几个星期几"的可选序号；5 = 当月最后一个该星期几</summary>
        public List<int> MonthlyNthOptions { get; } = new List<int> { 1, 2, 3, 4, 5 };

        // ===== 模型子对象（各类型互斥，未选中时该类为 null，绑定在各自的可见性块内）=====
        public TimeTrigger TimeTriggerObj => Task.Trigger as TimeTrigger;
        public SystemEventTrigger SystemTriggerObj => Task.Trigger as SystemEventTrigger;
        public FileWatcherTrigger FileTriggerObj => Task.Trigger as FileWatcherTrigger;

        // ===== 选中状态 =====
        [ObservableProperty] private TriggerType _selectedTriggerType;
        [ObservableProperty] private TimeTriggerKind? _triggerKind;
        [ObservableProperty] private SystemEventType? _systemEventType;
        [ObservableProperty] private IntervalFirstRunMode? _firstRunMode;
        [ObservableProperty] private MonthlyMode? _monthlyKind;

        /// <summary>“每周”的星期勾选（与条件里的“允许时段”各持一份，互不干扰）</summary>
        public ObservableCollection<WeekdayOption> WeekdayOptions { get; }

        /// <summary>是否显示进程名过滤（只有"进程启动 / 进程退出"两个系统事件用得上）</summary>
        public bool ShowProcessFilter
        {
            get
            {
                if (SelectedTriggerType != TriggerType.SystemEvent) return false;
                var e = SystemEventType;
                // 必须写全限定名：属性名 SystemEventType 会遮蔽同名类型，直接写 SystemEventType.ProcessStarted 编译不过
                return e == TaskScheduler.SystemEventType.ProcessStarted
                    || e == TaskScheduler.SystemEventType.ProcessStopped;
            }
        }

        /// <summary>
        /// 切换触发器类型时保留各类型下已经填好的配置：切走再切回来，内容还在。
        /// 旧实现直接 new 一个空实例 —— 用户填好"每天 09:00"，顺手切到"每周"看一眼，
        /// 切回来发现全空了。
        /// </summary>
        private readonly Dictionary<TriggerType, ITrigger> _cache = new Dictionary<TriggerType, ITrigger>();

        /// <summary>
        /// 构造期间为 true：这时候给 SelectedXxx 赋值只是"把模型现状回填到界面"，
        /// 不应该反过来去写模型（否则会把新建触发器的默认值盖到已有配置上）。
        /// </summary>
        private bool _initializing = true;

        partial void OnSelectedTriggerTypeChanged(TriggerType value)
        {
            if (_initializing) return;
            if (Task.Trigger != null && Task.Trigger.Type == value) return;

            if (Task.Trigger != null) _cache[Task.Trigger.Type] = Task.Trigger;
            Task.Trigger = _cache.TryGetValue(value, out var cached) ? cached : CreateTrigger(value);
            _cache[value] = Task.Trigger;

            RaiseTriggerProps();
            Host.NotifyStructureChanged();
        }

        partial void OnTriggerKindChanged(TimeTriggerKind? value)
        {
            if (_initializing) return;
            var t = TimeTriggerObj;
            if (t != null && value.HasValue) t.Kind = value.Value;

            // 一次性任务若没填时间，NextRunTime 算出来是 null → 永不执行、且界面上没有任何提示。
            // 切到"一次性"时给个合理默认值（明天 09:00），避免任务静默失效。
            if (t != null && value == TimeTriggerKind.OneTime && !t.TargetTime.HasValue)
            {
                t.TargetTime = DateTime.Now.Date.AddDays(1).AddHours(9);
                OnPropertyChanged(nameof(TimeTriggerObj)); // 模型类不发通知，手动刷新绑定
            }

            Host.NotifyStructureChanged();
        }

        partial void OnSystemEventTypeChanged(SystemEventType? value)
        {
            if (_initializing) return;
            var t = SystemTriggerObj;
            if (t != null && value.HasValue) t.EventType = value.Value;
            OnPropertyChanged(nameof(ShowProcessFilter));
            Host.NotifyStructureChanged();
        }

        partial void OnFirstRunModeChanged(IntervalFirstRunMode? value)
        {
            if (_initializing) return;
            var t = TimeTriggerObj;
            if (t != null && value.HasValue) t.FirstRunMode = value.Value;
            Host.NotifyStructureChanged();
        }

        partial void OnMonthlyKindChanged(MonthlyMode? value)
        {
            if (_initializing) return;
            var t = TimeTriggerObj;
            if (t != null && value.HasValue) t.MonthlyKind = value.Value;
            Host.NotifyStructureChanged();
        }

        private void RaiseTriggerProps()
        {
            OnPropertyChanged(nameof(TimeTriggerObj));
            OnPropertyChanged(nameof(SystemTriggerObj));
            OnPropertyChanged(nameof(FileTriggerObj));

            // 同步子类型代理，驱动各字段的可见性
            TriggerKind = TimeTriggerObj?.Kind;
            SystemEventType = SystemTriggerObj?.EventType;
            FirstRunMode = TimeTriggerObj?.FirstRunMode;
            MonthlyKind = TimeTriggerObj?.MonthlyKind;

            OnPropertyChanged(nameof(ShowProcessFilter));
            Refresh();
        }

        /// <summary>把模型现状同步到界面状态（构造时与外部改动后调用）</summary>
        private void SyncFromTask()
        {
            SelectedTriggerType = Task.Trigger?.Type ?? TriggerType.Time;
            if (Task.Trigger != null) _cache[SelectedTriggerType] = Task.Trigger;

            var tt = Task.Trigger as TimeTrigger;
            TriggerKind = tt?.Kind;
            FirstRunMode = tt?.FirstRunMode;
            MonthlyKind = tt?.MonthlyKind;
            SystemEventType = (Task.Trigger as SystemEventTrigger)?.EventType;

            WeekdayOptionBuilder.Rebuild(WeekdayOptions, tt?.WeeklyDays, ApplyWeeklyDays);
        }

        /// <summary>把界面上勾的星期写回触发器（勾选一变就调用，不必等点确定）</summary>
        public void ApplyWeeklyDays()
        {
            var tt = TimeTriggerObj;
            if (tt == null) return;
            tt.WeeklyDays = WeekdayOptionBuilder.Collect(WeekdayOptions);
        }

        /// <summary>点「确定」时兜底再同步一次，保证模型与界面一致</summary>
        public override void Commit()
        {
            ApplyWeeklyDays();
            Refresh();
        }

        private static ITrigger CreateTrigger(TriggerType t) => t switch
        {
            TriggerType.Time => new TimeTrigger(),
            TriggerType.SystemEvent => new SystemEventTrigger(),
            TriggerType.FileWatcher => new FileWatcherTrigger(),
            _ => new TimeTrigger()
        };
    }
}
