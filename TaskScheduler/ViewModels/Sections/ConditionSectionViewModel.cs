using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskScheduler.Conditions;

namespace TaskScheduler.ViewModels.Sections
{
    /// <summary>
    /// 「执行条件」分区：触发之后、动作之前必须全部满足的判定。
    /// 只读写 <see cref="AutomationTask.Conditions"/> 与任务级的条件等待窗口。
    /// </summary>
    public partial class ConditionSectionViewModel : SectionViewModelBase
    {
        public ConditionSectionViewModel(TaskEditorViewModel host) : base(host)
        {
            WindowWeekdayOptions = new ObservableCollection<WeekdayOption>();
            BrowseConditionPathCommand = new RelayCommand(() =>
            {
                // 条件里的路径既可能是文件也可能是目录，用"打开文件"对话框同时支持两者：
                // 选到文件就填文件，停在目录上直接确定则填该目录。
                var c = FileCond;
                if (c == null) return;
                var p = Pickers.File(c.Path, false);
                if (p != null) { c.Path = p; OnPropertyChanged(nameof(ConditionSummary)); }
            });
            SyncWindowWeekdays();
        }

        /// <summary>选条件里的路径（文件或目录）</summary>
        public ICommand BrowseConditionPathCommand { get; }

        public override string Title => "执行条件";

        public override string Summary
            => Task.Conditions == null || Task.Conditions.Count == 0
                ? "无条件（触发即执行）"
                : Task.Conditions.Count + " 个条件";

        // ===== 数据源 =====
        public List<ConditionType> ConditionTypes { get; } =
            Enum.GetValues(typeof(ConditionType)).Cast<ConditionType>().ToList();
        public List<PowerRequiredState> PowerStates { get; } =
            Enum.GetValues(typeof(PowerRequiredState)).Cast<PowerRequiredState>().ToList();
        public List<FileStateMode> FileStateModes { get; } =
            Enum.GetValues(typeof(FileStateMode)).Cast<FileStateMode>().ToList();
        public List<LoadMetric> LoadMetrics { get; } =
            Enum.GetValues(typeof(LoadMetric)).Cast<LoadMetric>().ToList();
        public List<FullScreenMode> FullScreenModes { get; } =
            Enum.GetValues(typeof(FullScreenMode)).Cast<FullScreenMode>().ToList();
        public List<NetworkKindType> NetworkKinds { get; } =
            Enum.GetValues(typeof(NetworkKindType)).Cast<NetworkKindType>().ToList();

        /// <summary>本机固定盘 / 可移动盘，供磁盘条件下拉</summary>
        public List<string> AvailableDrives { get; } = DiskSpaceCondition.GetDrives();

        // ===== 选中状态 =====
        [ObservableProperty] private ConditionType _newConditionType = ConditionType.NetworkConnected;

        [ObservableProperty] private ICondition _selectedCondition;

        /// <summary>
        /// 代理属性：始终走"顶层属性"路径（值可为 null），避免绑定 SelectedCondition.Type
        /// 在 SelectedCondition 为 null 时因嵌套路径失效而不调 Converter、回落到 Visible 的坑。
        /// </summary>
        [ObservableProperty] private ConditionType? _selectedConditionType;

        // ===== 各条件类型的强类型代理（未选中该类时为 null，绑定都在各自的可见性块内）=====
        public NetworkConnectedCondition NetCond => SelectedCondition as NetworkConnectedCondition;
        public PowerCondition PowerCond => SelectedCondition as PowerCondition;
        public ProcessRunningCondition ProcCond => SelectedCondition as ProcessRunningCondition;
        public LocalIpCondition LocalIpCond => SelectedCondition as LocalIpCondition;
        public DiskSpaceCondition DiskCond => SelectedCondition as DiskSpaceCondition;
        public FileStateCondition FileCond => SelectedCondition as FileStateCondition;
        public TimeWindowCondition WindowCond => SelectedCondition as TimeWindowCondition;
        public HttpHealthCondition HttpCond => SelectedCondition as HttpHealthCondition;
        public SystemLoadCondition LoadCond => SelectedCondition as SystemLoadCondition;
        public ForegroundWindowCondition FgCond => SelectedCondition as ForegroundWindowCondition;
        public NetworkTypeCondition NetTypeCond => SelectedCondition as NetworkTypeCondition;
        public ServiceStateCondition SvcCond => SelectedCondition as ServiceStateCondition;

        /// <summary>条件列表为空时给界面一个占位提示</summary>
        public bool HasNoConditions => Task.Conditions == null || Task.Conditions.Count == 0;

        /// <summary>
        /// 是否有选中的条件。用来控制"取反 / 小结"这些与具体条件无关的框是否显示 ——
        /// 不能靠 SelectedConditionType 判断：那个属性在没选中时也是 null，
        /// 语义上"没选中任何条件"和"选中的条件类型不匹配"是两回事，分开更清楚。
        /// </summary>
        public bool HasSelectedCondition => SelectedCondition != null;

        /// <summary>
        /// 选中条件的"取反"开关。
        /// 做成顶层代理是为了避开 SelectedCondition.Negate 这种嵌套路径：
        /// 中间为 null 时整条绑定静默失效、不调 Converter，控件会回落成默认可见 / 默认值。
        /// </summary>
        public bool SelectedConditionNegate
        {
            get => SelectedCondition?.Negate ?? false;
            set
            {
                var c = SelectedCondition;
                if (c == null || c.Negate == value) return;
                c.Negate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ConditionSummary));
                Host.NotifyStructureChanged();
            }
        }

        /// <summary>条件区顶部的一句话小结，让"取反 + 条件"的组合读起来是人话</summary>
        public string ConditionSummary
        {
            get
            {
                if (SelectedCondition == null) return "";
                return SelectedCondition.Negate
                    ? $"当前条件取反：必须【不】满足「{TrimNegate(SelectedCondition.Describe)}」时才执行"
                    : $"当前条件：必须满足「{SelectedCondition.Describe}」时才执行";
            }
        }

        private static string TrimNegate(string s)
            => s != null && s.StartsWith("非（") && s.EndsWith("）")
                ? s.Substring(2, s.Length - 3)
                : s;

        /// <summary>“允许时段”条件里的星期多选；与“每周”触发器的勾选各持一份，互不干扰</summary>
        public ObservableCollection<WeekdayOption> WindowWeekdayOptions { get; }

        /// <summary>当前本机 IPv4（逗号分隔），给"本机 IP"条件做对照，避免填错地址</summary>
        public string CurrentLocalIps
        {
            get
            {
                var list = LocalIpCondition.GetLocalIPv4();
                return list.Count == 0 ? "（未检测到已连接网卡）" : string.Join("，", list);
            }
        }

        /// <summary>
        /// 「本机 IP」条件的实时体检：逐条翻译用户填的表达式，并对照当前网卡报是否命中。
        ///
        /// 前缀写法（10.1.22.0/24）光看字符串根本看不出匹配到哪一段，
        /// 不给反馈的话用户只能等到任务该跑的时候才发现写错。
        /// </summary>
        public string IpPatternHint
        {
            get
            {
                if (LocalIpCond == null) return "";

                var raw = LocalIpCond.IpAddress;
                if (string.IsNullOrWhiteSpace(raw))
                    return "可填 IPv4 地址（10.1.22.19）、网段前缀（10.1.22.0/24）、"
                         + "裸前缀（10.1.22.）或通配（10.1.22.*）；多个用逗号分隔，命中任意一个即通过。";

                var good = new List<string>();
                var bad = new List<string>();
                foreach (var p in LocalIpCondition.ParseExpected(raw))
                {
                    var err = LocalIpCondition.Validate(p);
                    if (err == null) good.Add(p + "：" + LocalIpCondition.Explain(p));
                    else bad.Add(p + "（" + err + "）");
                }

                if (bad.Count > 0) return "无法识别 " + string.Join("；", bad);

                var local = LocalIpCondition.GetLocalIPv4();
                var hit = new List<string>();
                foreach (var ip in local)
                {
                    if (LocalIpCondition.Matches(ip, raw)) hit.Add(ip);
                }

                var head = "识别为 " + string.Join("；", good);
                return hit.Count > 0
                    ? head + "，当前本机命中 " + string.Join("、", hit)
                    : head + "，当前本机未命中（本机："
                           + (local.Count == 0 ? "未检测到已连接网卡" : string.Join("、", local)) + "）";
            }
        }

        /// <summary>IP 表达式是否全部可识别（有写错的条目时界面上标红）</summary>
        public bool IpPatternIsValid
        {
            get
            {
                if (LocalIpCond == null) return true;
                var raw = LocalIpCond.IpAddress;
                if (string.IsNullOrWhiteSpace(raw)) return true;

                foreach (var p in LocalIpCondition.ParseExpected(raw))
                {
                    if (LocalIpCondition.Validate(p) != null) return false;
                }
                return true;
            }
        }

        public ICommand AddConditionCommand => new RelayCommand(AddCondition);
        public ICommand RemoveConditionCommand => new RelayCommand<ICondition>(RemoveCondition);
        public ICommand ClearConditionsCommand => new RelayCommand(ClearConditions,
            () => Task.Conditions != null && Task.Conditions.Count > 0);

        /// <summary>切到该分区时刷新一遍本机 IP（网卡状态随时会变）</summary>
        public override void OnActivated()
        {
            OnPropertyChanged(nameof(CurrentLocalIps));
            OnPropertyChanged(nameof(IpPatternHint));
            OnPropertyChanged(nameof(IpPatternIsValid));
            OnPropertyChanged(nameof(HasNoConditions));
        }

        /// <summary>
        /// 正在监听属性变化的条件。存一份是为了换选中项时能准确摘掉旧订阅 ——
        /// OnSelectedConditionChanged 只拿得到新值，没有它就不知道要退订谁。
        /// </summary>
        private ICondition _watched;

        /// <summary>
        /// 条件字段被改了（在下面输入框里打字、点「浏览…」选路径）→ 刷新本分区派生的展示字段。
        /// 条件列表里那一行不归这里管：ConditionBase 的 SetField 会连带通知 Describe。
        /// </summary>
        private void OnWatchedConditionChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(ConditionSummary));
            OnPropertyChanged(nameof(IpPatternHint));
            OnPropertyChanged(nameof(IpPatternIsValid));
            Host.NotifyStructureChanged();
        }

        partial void OnSelectedConditionChanged(ICondition value)
        {
            if (!ReferenceEquals(_watched, value))
            {
                if (_watched is INotifyPropertyChanged oldOne)
                    oldOne.PropertyChanged -= OnWatchedConditionChanged;
                _watched = value;
                if (_watched is INotifyPropertyChanged newOne)
                    newOne.PropertyChanged += OnWatchedConditionChanged;
            }

            SelectedConditionType = value?.Type;
            OnPropertyChanged(nameof(NetCond));
            OnPropertyChanged(nameof(PowerCond));
            OnPropertyChanged(nameof(ProcCond));
            OnPropertyChanged(nameof(LocalIpCond));
            OnPropertyChanged(nameof(DiskCond));
            OnPropertyChanged(nameof(FileCond));
            OnPropertyChanged(nameof(WindowCond));
            OnPropertyChanged(nameof(HttpCond));
            OnPropertyChanged(nameof(LoadCond));
            OnPropertyChanged(nameof(FgCond));
            OnPropertyChanged(nameof(NetTypeCond));
            OnPropertyChanged(nameof(SvcCond));
            OnPropertyChanged(nameof(CurrentLocalIps));
            OnPropertyChanged(nameof(IpPatternHint));
            OnPropertyChanged(nameof(IpPatternIsValid));
            OnPropertyChanged(nameof(SelectedConditionNegate));
            OnPropertyChanged(nameof(ConditionSummary));
            OnPropertyChanged(nameof(HasSelectedCondition));
            SyncWindowWeekdays();
        }

        private void AddCondition()
        {
            var c = CreateCondition(NewConditionType);
            Task.Conditions.Add(c);
            SelectedCondition = c;
            OnPropertyChanged(nameof(HasNoConditions));
            Host.NotifyStructureChanged();
        }

        private void RemoveCondition(ICondition c)
        {
            if (c == null) return;
            Task.Conditions.Remove(c);
            SelectedCondition = null;
            OnPropertyChanged(nameof(HasNoConditions));
            Host.NotifyStructureChanged();
        }

        private void ClearConditions()
        {
            Task.Conditions.Clear();
            SelectedCondition = null;
            OnPropertyChanged(nameof(HasNoConditions));
            Host.NotifyStructureChanged();
        }

        /// <summary>按模型里的星期重建“允许时段”的勾选列表</summary>
        private void SyncWindowWeekdays()
            => WeekdayOptionBuilder.Rebuild(WindowWeekdayOptions, WindowCond?.Days, ApplyWindowDays);

        /// <summary>把“允许时段”的星期勾选写回条件（勾选一变就调用，见 WeekdayOption.Changed）</summary>
        public void ApplyWindowDays()
        {
            var c = WindowCond;
            if (c == null) return;
            c.Days = WeekdayOptionBuilder.Collect(WindowWeekdayOptions);
            OnPropertyChanged(nameof(ConditionSummary));
        }

        /// <summary>点「确定」时把界面上残留的状态写回模型</summary>
        public override void Commit()
        {
            ApplyWindowDays();
            Refresh();
        }

        private static ICondition CreateCondition(ConditionType t) => t switch
        {
            ConditionType.NetworkConnected => new NetworkConnectedCondition(),
            ConditionType.Power => new PowerCondition(),
            ConditionType.ProcessRunning => new ProcessRunningCondition(),
            ConditionType.LocalIp => new LocalIpCondition(),
            ConditionType.DiskSpace => new DiskSpaceCondition(),
            ConditionType.FilePath => new FileStateCondition(),
            ConditionType.TimeWindow => new TimeWindowCondition(),
            ConditionType.HttpHealth => new HttpHealthCondition(),
            ConditionType.SystemLoad => new SystemLoadCondition(),
            ConditionType.ForegroundWindow => new ForegroundWindowCondition(),
            ConditionType.NetworkType => new NetworkTypeCondition(),
            ConditionType.ServiceState => new ServiceStateCondition(),
            _ => new NetworkConnectedCondition()
        };
    }
}
