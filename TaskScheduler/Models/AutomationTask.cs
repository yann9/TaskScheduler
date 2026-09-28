using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TaskScheduler.Conditions;
using TaskScheduler.Triggers;

namespace TaskScheduler
{
    /// <summary>
    /// 一个自动化任务：一个触发器 + 一个动作。
    /// 实现了 INotifyPropertyChanged，便于 WPF 数据网格实时刷新。
    /// </summary>
    public class AutomationTask : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        private string _name = "新任务";
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnChanged(); } }
        }

        private bool _enabled = true;
        public bool Enabled
        {
            get => _enabled;
            set { if (_enabled != value) { _enabled = value; OnChanged(); } }
        }

        public ITrigger Trigger { get; set; }
        public Actions.IAction Action { get; set; }

        private string _note = "";
        /// <summary>备注：给自己看的说明（为什么这么配、依赖哪台机器）。不参与任何调度逻辑。</summary>
        public string Note
        {
            get => _note;
            set { if (_note != value) { _note = value; OnChanged(); } }
        }

        /// <summary>执行条件：触发后、动作前需全部满足（空列表表示无条件）</summary>
        /// <summary>
        /// 执行条件列表。必须是 ObservableCollection：界面直接绑这个集合，
        /// 用普通 List 时增删不会通知 UI（点了删除数据没了、列表项还在）。
        /// </summary>
        public ObservableCollection<ICondition> Conditions { get; set; } = new ObservableCollection<ICondition>();

        /// <summary>
        /// 条件等待窗口（秒）：触发后条件不满足时不立即放弃，而是每 ConditionRetrySeconds 秒重试一次，
        /// 直到条件满足、或窗口耗尽才判定跳过本次。
        /// 0 = 不等待（一次不满足就跳过，等同旧行为）。
        /// 主要救两类场景：DHCP 还没续上 IP、笔记本合盖那一刻刚好没插电 —— 等几十秒就满足了，
        /// 一次判死会白白错过一整个周期（间隔 4 小时的任务就是再等 4 小时）。
        /// </summary>
        public int ConditionWaitSeconds { get; set; }

        /// <summary>等待窗口内的重试间隔（秒），实际生效值不小于 1</summary>
        public int ConditionRetrySeconds { get; set; } = 15;

        private System.DateTime? _lastRunTime;
        public System.DateTime? LastRunTime
        {
            get => _lastRunTime;
            set { if (_lastRunTime != value) { _lastRunTime = value; OnChanged(); } }
        }

        private string _lastRunResult = "";
        public string LastRunResult
        {
            get => _lastRunResult;
            set { if (_lastRunResult != value) { _lastRunResult = value; OnChanged(); } }
        }

        private System.DateTime? _nextRunTime;
        public System.DateTime? NextRunTime
        {
            get => _nextRunTime;
            set { if (_nextRunTime != value) { _nextRunTime = value; OnChanged(); } }
        }

        /// <summary>触发器描述（仅用于界面展示，不参与序列化）</summary>
        [Newtonsoft.Json.JsonIgnore]
        public string TriggerDescription => Trigger?.Describe() ?? "(未配置)";

        /// <summary>动作描述（仅用于界面展示，不参与序列化）</summary>
        [Newtonsoft.Json.JsonIgnore]
        public string ActionDescription => Action?.Describe() ?? "(未配置)";

        /// <summary>条件描述（仅用于界面展示，不参与序列化）</summary>
        [Newtonsoft.Json.JsonIgnore]
        public string ConditionDescription
            => Conditions == null || Conditions.Count == 0
                ? "无条件"
                : string.Join("；", Conditions.Select(c => c.Describe));

        [Newtonsoft.Json.JsonIgnore]
        public string NextRunText => NextRunTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

        [Newtonsoft.Json.JsonIgnore]
        public string StatusText => Enabled ? "已启用" : "已停用";

        /// <summary>
        /// 上次运行是否失败（界面上把这一行标红）。
        /// 直接读 LastRunResult 的前缀而不是另存一个标志位：少一个需要两边同步的状态。
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool IsLastRunFailed
            => !string.IsNullOrEmpty(LastRunResult)
               && (LastRunResult.StartsWith("失败", System.StringComparison.Ordinal)
                   || LastRunResult.StartsWith("异常", System.StringComparison.Ordinal));

        /// <summary>触发器/动作被替换后，主动通知界面刷新展示字段</summary>
        public void RefreshView()
        {
            OnChanged(nameof(TriggerDescription));
            OnChanged(nameof(ActionDescription));
            OnChanged(nameof(ConditionDescription));
            OnChanged(nameof(NextRunText));
            OnChanged(nameof(StatusText));
            OnChanged(nameof(IsLastRunFailed));
        }

        /// <summary>
        /// 深拷贝（JSON 往返，复用存档用的序列化设置 + 白名单绑定器）。
        ///
        /// 编辑器必须拿副本：TaskEditorViewModel 的所有绑定都是直接写模型的，
        /// 若把引擎正在持有的实例交给编辑器，用户点「取消」也只是关窗口 ——
        /// 改动早已落在这个对象上，之后引擎任意一次 Save()（例如排期变化落盘、
        /// 退出时 Stop+Save）都会把这份半截改动持久化，用户以为取消了其实没有。
        ///
        /// 带 JsonIgnore 的运行时状态（定时器、错过补偿计划点）不会复制，正是想要的效果。
        /// </summary>
        public AutomationTask Clone()
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(this, JsonSafety.Persistent);
            return Newtonsoft.Json.JsonConvert.DeserializeObject<AutomationTask>(json, JsonSafety.Persistent);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged([CallerMemberName] string member = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(member));
    }
}
