using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskScheduler.Engine;
using TaskScheduler.Views;

namespace TaskScheduler.ViewModels
{
    /// <summary>运行记录窗口的任务筛选下拉项；Id 为 null 表示"全部任务"</summary>
    public class TaskFilterOption
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public override string ToString() => Name;
    }

    /// <summary>运行记录窗口的结果筛选下拉项；Value 为 null 表示"全部结果"</summary>
    public class OutcomeFilterOption
    {
        public RunOutcome? Value { get; set; }
        public string Label { get; set; }
    }

    /// <summary>
    /// 运行记录窗口：每次执行（含"跳过"）都留一条，可按任务 / 结果 / 关键字筛。
    ///
    /// 只保留最近 N 条（N 在设置里改），所以这是个"近期排障"视图，不是永久审计日志。
    /// </summary>
    public partial class RunHistoryViewModel : ObservableObject
    {
        private readonly ITaskService _host;
        private readonly DispatcherTimer _timer;

        public ObservableCollection<RunRecord> Records { get; } = new ObservableCollection<RunRecord>();
        public ObservableCollection<TaskFilterOption> TaskFilters { get; } = new ObservableCollection<TaskFilterOption>();

        public List<OutcomeFilterOption> OutcomeFilters { get; } = new List<OutcomeFilterOption>
        {
            new OutcomeFilterOption { Value = null, Label = "全部结果" },
            new OutcomeFilterOption { Value = RunOutcome.Success, Label = "成功" },
            new OutcomeFilterOption { Value = RunOutcome.Failed, Label = "失败" },
            new OutcomeFilterOption { Value = RunOutcome.Skipped, Label = "已跳过" },
            new OutcomeFilterOption { Value = RunOutcome.Canceled, Label = "已取消" },
        };

        public List<int> LimitOptions { get; } = new List<int> { 100, 200, 500, 1000, 5000 };

        [ObservableProperty] private TaskFilterOption _selectedTaskFilter;
        [ObservableProperty] private OutcomeFilterOption _selectedOutcomeFilter;
        [ObservableProperty] private string _keyword = "";
        [ObservableProperty] private RunRecord _selectedRecord;
        [ObservableProperty] private bool _autoRefresh = true;
        [ObservableProperty] private int _limit = 500;
        [ObservableProperty] private string _statusText = "";

        public ICommand RefreshCommand { get; }
        public ICommand ClearCommand { get; }

        public RunHistoryViewModel(ITaskService host, string initialTaskId = null)
        {
            _host = host;
            SelectedOutcomeFilter = OutcomeFilters[0];

            RefreshCommand = new RelayCommand(() => { ReloadTaskFilters(); Load(); });
            ClearCommand = new RelayCommand(Clear);

            ReloadTaskFilters();
            SelectTask(initialTaskId);
            Load();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += (s, e) => { if (AutoRefresh) Load(); };
            _timer.Start();
        }

        partial void OnSelectedTaskFilterChanged(TaskFilterOption value) => Load();
        partial void OnSelectedOutcomeFilterChanged(OutcomeFilterOption value) => Load();
        partial void OnLimitChanged(int value) => Load();
        partial void OnKeywordChanged(string value) => Load();

        /// <summary>外部（主窗口"查看该任务记录"）指定只看某个任务</summary>
        public void SelectTask(string taskId)
        {
            var target = TaskFilters.FirstOrDefault(f => f.Id == taskId) ?? TaskFilters.FirstOrDefault();
            if (!ReferenceEquals(target, SelectedTaskFilter))
            {
                SelectedTaskFilter = target; // setter 会触发一次 Load
            }
        }

        /// <summary>重建任务下拉：任务可能刚被增删，每次手动刷新时重来一遍</summary>
        private void ReloadTaskFilters()
        {
            var keep = SelectedTaskFilter?.Id;

            TaskFilters.Clear();
            TaskFilters.Add(new TaskFilterOption { Id = null, Name = "全部任务" });
            try
            {
                foreach (var t in _host.ListTasks())
                    TaskFilters.Add(new TaskFilterOption { Id = t.Id, Name = t.Name });
            }
            catch (Exception ex)
            {
                Log.Error("读取任务列表失败：" + ex.Message);
            }

            var restored = TaskFilters.FirstOrDefault(f => f.Id == keep) ?? TaskFilters[0];
            if (!ReferenceEquals(restored, SelectedTaskFilter)) SelectedTaskFilter = restored;
        }

        private void Load()
        {
            var keepId = SelectedRecord?.Id;

            IReadOnlyList<RunRecord> list;
            try
            {
                list = _host.GetRunHistory(
                    SelectedTaskFilter?.Id,
                    SelectedOutcomeFilter?.Value,
                    Keyword,
                    Limit);
            }
            catch (Exception ex)
            {
                Log.Error("读取运行记录失败：" + ex.Message);
                list = new List<RunRecord>();
            }

            Records.Clear();
            foreach (var r in list) Records.Add(r);

            if (keepId != null) SelectedRecord = Records.FirstOrDefault(r => r.Id == keepId);

            var failed = Records.Count(r => r.Outcome == RunOutcome.Failed);
            StatusText = $"{Records.Count} 条记录" + (failed > 0 ? $"，其中失败 {failed} 条" : "");
        }

        private void Clear()
        {
            var taskName = SelectedTaskFilter?.Id == null ? "全部任务" : SelectedTaskFilter.Name;
            var r = TsDialog.Show(
                $"确认清空「{taskName}」的运行记录？\n\n只删记录，不影响任务本身。",
                "确认", TsDialogButtons.YesNo, TsDialogIcon.Question);
            if (r != System.Windows.MessageBoxResult.Yes) return;

            try
            {
                _host.ClearRunHistory(SelectedTaskFilter?.Id);
                Load();
            }
            catch (Exception ex)
            {
                TsDialog.Show("清空运行记录失败：\n\n" + ex.Message, "提示",
                    TsDialogButtons.Ok, TsDialogIcon.Warning);
            }
        }
    }
}
