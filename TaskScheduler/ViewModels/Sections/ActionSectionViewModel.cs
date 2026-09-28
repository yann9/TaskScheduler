using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskScheduler.Actions;

namespace TaskScheduler.ViewModels.Sections
{
    /// <summary>
    /// 「动作」分区：触发且条件满足之后具体做什么。
    /// 只读写 <see cref="AutomationTask.Action"/>。
    /// </summary>
    public partial class ActionSectionViewModel : SectionViewModelBase
    {
        public ActionSectionViewModel(TaskEditorViewModel host) : base(host)
        {
            BrowseProgramCommand = new RelayCommand(() =>
            {
                var target = RunActionObj;
                if (target == null) return;
                var p = Pickers.File(target.Program, true);
                if (p != null) target.Program = p;
            });
            BrowseWorkingDirCommand = new RelayCommand(() =>
            {
                var target = RunActionObj;
                if (target == null) return;
                var p = Pickers.Folder(target.WorkingDirectory);
                if (p != null) target.WorkingDirectory = p;
            });
            BrowseSourceCommand = new RelayCommand(() =>
            {
                var target = FileActionObj;
                if (target == null) return;
                var p = Pickers.File(target.Source, true);
                if (p != null) target.Source = p;
            });
            BrowseDestCommand = new RelayCommand(() =>
            {
                var target = FileActionObj;
                if (target == null) return;
                var p = Pickers.File(target.Destination, false);
                if (p != null) target.Destination = p;
            });

            SyncFromTask();
            WatchAction(Task.Action);
            _initializing = false;
        }

        public override string Title => "动作";

        public override string Summary
        {
            get
            {
                var d = Task.Action?.Describe() ?? "（未配置）";
                return d.Length <= 24 ? d : d.Substring(0, 24) + "…";
            }
        }

        // ===== 数据源 =====
        public List<ActionType> ActionTypes { get; } =
            Enum.GetValues(typeof(ActionType)).Cast<ActionType>().ToList();
        public List<HttpMethodKind> HttpMethods { get; } =
            Enum.GetValues(typeof(HttpMethodKind)).Cast<HttpMethodKind>().ToList();
        public List<FileOperationKind> FileOps { get; } =
            Enum.GetValues(typeof(FileOperationKind)).Cast<FileOperationKind>().ToList();
        public List<InputActionKind> InputKinds { get; } =
            Enum.GetValues(typeof(InputActionKind)).Cast<InputActionKind>().ToList();

        // ===== 模型子对象（各类型互斥）=====
        public RunProgramAction RunActionObj => Task.Action as RunProgramAction;
        public HttpAction HttpActionObj => Task.Action as HttpAction;
        public NotificationAction NotifyActionObj => Task.Action as NotificationAction;
        public FileAction FileActionObj => Task.Action as FileAction;
        public InputAction InputActionObj => Task.Action as InputAction;

        [ObservableProperty] private ActionType _selectedActionType;

        // ===== 路径浏览（避免手敲路径，减少误操作）=====
        public ICommand BrowseProgramCommand { get; }
        public ICommand BrowseWorkingDirCommand { get; }
        public ICommand BrowseSourceCommand { get; }
        public ICommand BrowseDestCommand { get; }

        /// <summary>
        /// 切换动作类型时保留各类型下已填好的配置：切走再切回来内容还在。
        /// （旧实现直接 new 空实例，用户填好"运行程序"的路径、顺手看一眼"HTTP 请求"，
        /// 切回来发现全空了。）
        /// </summary>
        private readonly Dictionary<ActionType, IAction> _cache = new Dictionary<ActionType, IAction>();

        /// <summary>构造期间为 true：给 SelectedActionType 赋值只是回填界面，不该反过来写模型</summary>
        private bool _initializing = true;

        partial void OnSelectedActionTypeChanged(ActionType value)
        {
            if (_initializing) return;
            if (Task.Action != null && Task.Action.Type == value) return;

            if (Task.Action != null) _cache[Task.Action.Type] = Task.Action;
            Task.Action = _cache.TryGetValue(value, out var cached) ? cached : CreateAction(value);
            _cache[value] = Task.Action;

            WatchAction(Task.Action);
            RaiseActionProps();
            Host.NotifyStructureChanged();
        }

        /// <summary>正在监听属性变化的动作对象（换类型时要先摘掉旧订阅）</summary>
        private IAction _watched;

        /// <summary>
        /// 跟着当前动作，字段一变就刷新左侧导航的摘要。
        /// 文本框是直接写模型的，ViewModel 收不到通知 —— 只能订阅模型。
        /// </summary>
        private void WatchAction(IAction action)
        {
            if (ReferenceEquals(_watched, action)) return;
            if (_watched is INotifyPropertyChanged oldOne) oldOne.PropertyChanged -= OnWatchedActionChanged;
            _watched = action;
            if (_watched is INotifyPropertyChanged newOne) newOne.PropertyChanged += OnWatchedActionChanged;
        }

        private void OnWatchedActionChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Summary));
            Host.NotifyStructureChanged();
        }

        private void RaiseActionProps()
        {
            OnPropertyChanged(nameof(RunActionObj));
            OnPropertyChanged(nameof(HttpActionObj));
            OnPropertyChanged(nameof(NotifyActionObj));
            OnPropertyChanged(nameof(FileActionObj));
            OnPropertyChanged(nameof(InputActionObj));
            Refresh();
        }

        private void SyncFromTask()
        {
            SelectedActionType = Task.Action?.Type ?? ActionType.RunProgram;
            if (Task.Action != null) _cache[SelectedActionType] = Task.Action;
        }

        private static IAction CreateAction(ActionType a) => a switch
        {
            ActionType.RunProgram => new RunProgramAction(),
            ActionType.HttpRequest => new HttpAction(),
            ActionType.Notification => new NotificationAction(),
            ActionType.FileOperation => new FileAction(),
            ActionType.InputSimulation => new InputAction(),
            _ => new RunProgramAction()
        };
    }
}
