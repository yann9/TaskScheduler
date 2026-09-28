using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskScheduler.Engine;
using TaskScheduler.Service;
using TaskScheduler.Triggers;
using TaskScheduler.Views;

namespace TaskScheduler.ViewModels
{
    /// <summary>
    /// 主窗口：任务列表 + 各功能窗口的入口。
    ///
    /// 这里刻意不做"看日志 / 看运行记录"的展示逻辑 —— 那两件事各自有独立窗口，
    /// 主窗口只负责列表和操作，保持一眼能看清任务全貌。
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        private readonly ITaskService _host;
        private readonly DispatcherTimer _timer;

        /// <summary>已打开的辅助窗口（避免重复点"日志"开出一堆一模一样的窗口）</summary>
        private Views.LogWindow _logWindow;
        private Views.RunHistoryWindow _historyWindow;

        public ObservableCollection<AutomationTask> Tasks { get; } = new ObservableCollection<AutomationTask>();

        [ObservableProperty] private string _engineState = "未运行";
        [ObservableProperty] private string _lastEvent = "";
        [ObservableProperty] private AutomationTask _selectedTask;
        [ObservableProperty] private string _hostMode = "本地模式";
        [ObservableProperty] private string _serviceState = "未知";
        [ObservableProperty] private int _taskListFontSize = 12;

        public ICommand NewCommand { get; }
        public ICommand EditCommand { get; }
        public ICommand DuplicateCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand ToggleCommand { get; }
        public ICommand RunNowCommand { get; }
        public ICommand StartEngineCommand { get; }
        public ICommand StopEngineCommand { get; }
        public ICommand ExitCommand { get; }
        public ICommand RefreshCommand { get; }

        // 服务类命令用 IRelayCommand 而不是 ICommand：界面上的启用状态完全跟着
        // 服务的安装 / 运行状态走，需要能主动触发一次 CanExecute 重算
        //（CommandManager 的自动重算依赖输入焦点变化，菜单打开时并不可靠）。
        public IRelayCommand InstallServiceCommand { get; }
        public IRelayCommand UninstallServiceCommand { get; }
        public IRelayCommand StartServiceCommand { get; }
        public IRelayCommand StopServiceCommand { get; }
        public ICommand OpenLogWindowCommand { get; }
        public ICommand OpenRunHistoryCommand { get; }
        public ICommand OpenSettingsCommand { get; }

        public MainViewModel(ITaskService host, bool serviceMode)
        {
            _host = host;
            HostMode = serviceMode ? "服务模式（后台服务托管）" : "本地模式（本进程托管）";

            NewCommand = new RelayCommand(NewTask);
            EditCommand = new RelayCommand(EditTask, () => SelectedTask != null);
            DuplicateCommand = new RelayCommand(DuplicateTask, () => SelectedTask != null);
            DeleteCommand = new RelayCommand(DeleteTask, () => SelectedTask != null);
            ToggleCommand = new RelayCommand(ToggleTask, () => SelectedTask != null);
            RunNowCommand = new RelayCommand(RunNow, () => SelectedTask != null);
            StartEngineCommand = new RelayCommand(StartLocalEngineGuarded, () => !_host.IsEngineRunning);
            StopEngineCommand = new RelayCommand(() => Guard(_host.StopEngine, "停止引擎"), () => _host.IsEngineRunning);
            ExitCommand = new RelayCommand(Exit);
            RefreshCommand = new RelayCommand(() => { Reload(); RefreshServiceState(); });
            // 四个服务命令的可用条件，对应的都是"点了会失败"或"点了没事干"的情形：
            //   安装：只有没装过才需要。特例是"装过但注册路径不是当前程序"——那属于坏状态，必须留修复入口。
            //   卸载：没装就没什么可卸的。
            //   启动：已装且当前没在跑。
            //   停止：已装且当前在跑。
            // 提权子进程还没回来时（_serviceBusy）全部禁掉，避免连点弹出一串 UAC。
            InstallServiceCommand = new RelayCommand(
                () => Elevated("--install-service", "安装后台服务"),
                () => !_serviceBusy && (!_serviceInstalled || _serviceNeedsRepair));
            UninstallServiceCommand = new RelayCommand(
                () => Elevated("--uninstall-service", "卸载后台服务"),
                () => !_serviceBusy && _serviceInstalled);
            StartServiceCommand = new RelayCommand(
                () => Elevated("--start-service", "启动后台服务"),
                () => !_serviceBusy && _serviceInstalled && !_serviceRunning);
            StopServiceCommand = new RelayCommand(
                () => Elevated("--stop-service", "停止后台服务"),
                () => !_serviceBusy && _serviceInstalled && _serviceRunning);
            OpenLogWindowCommand = new RelayCommand(OpenLogWindow);
            OpenRunHistoryCommand = new RelayCommand(() => OpenRunHistory(null));
            OpenSettingsCommand = new RelayCommand(OpenSettings);

            Reload();
            RefreshServiceState();
            ApplySettings();

            // 设置里改了"任务列表字号"这类界面项，主窗口要立刻跟上
            SettingsService.Changed += OnSettingsChanged;

            // 服务模式下每次刷新是「两次管道往返 + 一次 SCM 查询」，
            // 每秒一遍纯属浪费，放慢到 3 秒；本地模式也只是内存读取，2 秒足够。
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(serviceMode ? 3 : 2) };
            _timer.Tick += (s, e) =>
            {
                // 任务由引擎/服务执行，"上次运行/结果/下次运行"必须靠定时重载才更新
                //（此前只刷状态栏，列表数据一直停在打开时的快照——周工实锤）。
                // 管道瞬断（服务重启中）不能带崩 UI，吞掉记日志即可。
                try { Reload(); }
                catch (Exception ex) { Log.Error("定时刷新任务列表失败：" + ex.Message); }
                RefreshLastEvent();
                RefreshServiceState();
            };
            _timer.Start();
            RefreshLastEvent();
        }

        /// <summary>
        /// 「启动引擎」的双跑守卫：后台服务正在运行时，再起本地引擎就是
        /// 两个引擎重复执行同一批任务 —— 必须让用户知情后二选一
        ///（周工规则：服务启用了就用服务）。确认逻辑在 App.ConfirmLocalEngineStart，
        /// 托盘菜单走的是同一个方法。
        /// </summary>
        private void StartLocalEngineGuarded()
        {
            if (!App.ConfirmLocalEngineStart()) return;
            Guard(_host.StartEngine, "启动引擎");
        }

        /// <summary>
        /// 把宿主调用包起来。服务模式下宿主的写操作失败会抛异常（后台服务拒绝 / 管道断开），
        /// 界面必须把它变成看得见的提示，而不是"点了没反应"。
        /// </summary>
        private bool Guard(Action action, string what)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"{what}失败：" + ex.Message);
                TsDialog.Show(
                    $"{what}失败：\n\n{ex.Message}", "提示",
                    TsDialogButtons.Ok, TsDialogIcon.Warning);
                return false;
            }
        }

        // ================= 列表 =================

        /// <summary>上次列表数据快照：定时器无变化时跳过重建，避免 2~3 秒一次的整表闪烁</summary>
        private string _tasksSnapshot = "";

        /// <summary>重新加载任务列表，并尽量保住当前选中项</summary>
        private void Reload(bool force = false)
        {
            var list = _host.ListTasks();
            // 快照只含列表展示的字段（启用/上次运行/结果/下次运行/名称）——
            // 任务执行后这些会变，变化了才值得重建整个集合
            var snap = string.Join("|", list.Select(t =>
                $"{t.Id}:{t.Enabled}:{t.LastRunTime?.Ticks}:{t.LastRunResult}:{t.NextRunText}:{t.Name}"));
            if (!force && snap == _tasksSnapshot) return;
            _tasksSnapshot = snap;

            var keepId = SelectedTask?.Id;
            Tasks.Clear();
            foreach (var t in list) Tasks.Add(t);

            // 重建列表会把 SelectedTask 置空 → "编辑/删除/立即运行"全部变灰，用户得重新点一次。
            if (keepId != null) SelectedTask = Tasks.FirstOrDefault(t => t.Id == keepId);

            UpdateEngineState();
        }

        private void SelectById(string id)
            => SelectedTask = Tasks.FirstOrDefault(t => t.Id == id);

        private void UpdateEngineState()
        {
            EngineState = _host.IsEngineRunning ? "运行中" : "已停止";
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>状态栏的"最近动态"：只取日志最后一行，够用来确认程序在动</summary>
        private void RefreshLastEvent()
        {
            var text = _host.GetLogs() ?? "";
            var idx = text.LastIndexOf('\n');
            var line = (idx >= 0 ? text.Substring(idx + 1) : text).Trim();
            if (!string.Equals(line, LastEvent, StringComparison.Ordinal)) LastEvent = line;
            UpdateEngineState();
        }

        // ===== 服务状态缓存：菜单项的启用判断全靠这三个标志 =====
        private bool _serviceInstalled;
        private bool _serviceRunning;
        private bool _serviceNeedsRepair;
        private bool _serviceBusy;

        /// <summary>
        /// 「服务」菜单顶部那行状态说明 —— 菜单项变灰时得让人知道为什么灰
        ///（是"没装"，还是"正在处理"）。
        ///
        /// 这里读的是缓存字段，不在 getter 里去查 SCM：这个属性每两三秒就会
        /// 因为状态刷新被 WPF 重读一次，每次真去查一遍服务管理器纯属浪费。
        /// </summary>
        public string ServiceMenuStatus => _serviceMenuStatus;
        private string _serviceMenuStatus = "当前：检测中…";

        /// <summary>状态栏那份文字（"运行中"/"已停止"/"未安装"）</summary>
        private string _serviceStatusText = "未知";

        private string ComposeServiceMenuStatus()
        {
            if (_serviceBusy) return "当前：正在处理服务操作…";
            if (!_serviceInstalled) return "当前：未安装后台服务";
            if (_serviceNeedsRepair) return "当前：已安装，但注册的路径不是当前程序（用「安装服务」修复）";
            if (_serviceRunning) return "当前：已安装，正在运行";
            return "当前：已安装，" + _serviceStatusText;
        }

        /// <summary>服务状态枚举 → 中文（状态栏和提权结果提示共用，避免一处中文一处英文）</summary>
        private static string StatusText(ServiceControllerStatus st) => st switch
        {
            ServiceControllerStatus.Running => "运行中",
            ServiceControllerStatus.Stopped => "已停止",
            ServiceControllerStatus.StartPending => "正在启动",
            ServiceControllerStatus.StopPending => "正在停止",
            ServiceControllerStatus.Paused => "已暂停",
            ServiceControllerStatus.PausePending => "正在暂停",
            ServiceControllerStatus.ContinuePending => "正在恢复",
            _ => st.ToString()
        };

        private void RefreshServiceState()
        {
            var st = ServiceControl.QueryStatus();

            _serviceInstalled = st != null;
            _serviceRunning = st == ServiceControllerStatus.Running;
            _serviceNeedsRepair = _serviceInstalled && ServiceControl.RegisteredForDifferentPath();

            _serviceStatusText = st == null ? "未安装" : StatusText(st.Value);
            ServiceState = _serviceStatusText;

            // 只在文字真的变了才通知：这个方法每两三秒被调一次，
            // 无条件通知等于让 WPF 白白重算一遍菜单绑定。
            var menu = ComposeServiceMenuStatus();
            if (!string.Equals(menu, _serviceMenuStatus, StringComparison.Ordinal))
            {
                _serviceMenuStatus = menu;
                OnPropertyChanged(nameof(ServiceMenuStatus));
            }

            NotifyServiceCommands();

            // 互斥收口放这里：本方法由定时器每两三秒驱动一次，
            // 是"服务状态变化"最及时的观测点（见 EnforceSingleEngine 的说明）
            EnforceSingleEngine();
        }

        /// <summary>
        /// 互斥规则的运行期兜底（周工规则：服务启用了就用服务）。
        ///
        /// 本地托管模式下，一旦发现后台服务已经 Running，就把本地引擎停掉 ——
        /// 覆盖两条启动时判断不了、只能事后收敛的路径：
        ///   1) 开机竞态：服务 start=auto 与登录自启并行，界面探测管道时服务还没就绪，
        ///      退化成本地托管，几秒后服务跑起来了；
        ///   2) 用户在程序之外（sc / services.msc）手动把服务启动起来。
        /// 收敛前先 Ping 一次管道：服务状态显示 Running 但管道还没建出来时，
        /// 再等一个刷新周期，别把用户切到"没有执行者"的状态。
        /// 幂等：本地引擎停了之后 IsRunning=false，这里不再触发。
        /// </summary>
        private void EnforceSingleEngine()
        {
            var engine = App.Engine;
            if (engine == null || !engine.IsRunning) return;   // 非本地托管，或引擎本来就没跑
            if (!_serviceInstalled || !_serviceRunning) return; // 服务没在跑，本地引擎是合法的

            bool pipeOk;
            try { using (var p = new Ipc.PipeClient()) pipeOk = p.TryPing(800); }
            catch { pipeOk = false; }
            if (!pipeOk) return;

            try
            {
                engine.Stop();
                Log.Write("检测到后台服务已运行，本地引擎已自动停止（服务启用了就用服务）");
                HostMode = "本地模式（引擎已由后台服务接管，重启程序后完全切换）";
                try
                {
                    App.Tray?.ShowNotification("任务已由后台服务接管",
                        "检测到后台服务正在运行，本地引擎已自动停止，任务不会重复执行。");
                }
                catch { }
            }
            catch (Exception ex)
            {
                Log.Error("自动停用本地引擎失败：" + ex.Message);
            }
        }

        /// <summary>让四个服务菜单项重新算一次启用状态</summary>
        private void NotifyServiceCommands()
        {
            InstallServiceCommand.NotifyCanExecuteChanged();
            UninstallServiceCommand.NotifyCanExecuteChanged();
            StartServiceCommand.NotifyCanExecuteChanged();
            StopServiceCommand.NotifyCanExecuteChanged();
        }

        private void ApplySettings()
        {
            TaskListFontSize = SettingsService.Current.TaskListFontSize;
        }

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d == null) return;
            if (d.CheckAccess()) ApplySettings();
            else d.BeginInvoke(new Action(ApplySettings));
        }

        // ================= 任务操作 =================

        private void NewTask()
        {
            var vm = new TaskEditorViewModel();
            if (!ShowEditor(vm)) return;

            if (Guard(() => _host.SaveTask(vm.Task), "保存任务"))
            {
                Reload();
                SelectById(vm.Task.Id);
            }
        }

        private void EditTask()
        {
            if (SelectedTask == null) return;

            // 编辑器拿到的必须是副本。编辑器里所有绑定都是直接写模型的，
            // 直接把引擎正在持有的实例交出去，用户点「取消」也只是关了窗口 ——
            // 改动早已落在这个对象上，之后引擎任意一次 Save() 都会把它持久化。
            AutomationTask draft;
            try
            {
                draft = SelectedTask.Clone();
            }
            catch (Exception ex)
            {
                TsDialog.Show(
                    "无法打开任务编辑器：复制任务配置失败。\n\n" + ex.Message,
                    "提示", TsDialogButtons.Ok, TsDialogIcon.Warning);
                return;
            }
            if (draft == null) return;

            var vm = new TaskEditorViewModel(draft);
            if (!ShowEditor(vm)) return; // 取消：draft 直接丢弃

            vm.ResetSchedule();
            if (Guard(() => _host.SaveTask(vm.Task), "保存任务")) Reload();
        }

        /// <summary>复制一份任务（改一个参数做变体时，比重头配一遍快得多）</summary>
        private void DuplicateTask()
        {
            if (SelectedTask == null) return;

            AutomationTask copy;
            try { copy = SelectedTask.Clone(); }
            catch (Exception ex)
            {
                TsDialog.Show("复制失败：\n\n" + ex.Message, "提示",
                    TsDialogButtons.Ok, TsDialogIcon.Warning);
                return;
            }
            if (copy == null) return;

            // 新身份 + 干净的运行状态：副本不应该带着原任务的执行历史
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = SelectedTask.Name + " - 副本";
            copy.LastRunResult = "";
            copy.LastRunTime = null;
            copy.NextRunTime = null;
            copy.RefreshView();
            if (copy.Trigger is TimeTrigger tt) tt.NextRunTime = null;

            if (Guard(() => _host.SaveTask(copy), "复制任务"))
            {
                Reload();
                SelectById(copy.Id);
            }
        }

        private void DeleteTask()
        {
            if (SelectedTask == null) return;

            if (SettingsService.Current.ConfirmOnDelete)
            {
                var r = TsDialog.Show(
                    $"确认删除任务「{SelectedTask.Name}」？\n\n删掉之后无法恢复（运行记录会保留）。", "确认删除",
                    TsDialogButtons.YesNo, TsDialogIcon.Question);
                if (r != System.Windows.MessageBoxResult.Yes) return;
            }

            if (Guard(() => _host.DeleteTask(SelectedTask), "删除任务")) Reload();
        }

        /// <summary>启用 / 暂时停用。停用只影响调度，任务配置原样保留。</summary>
        private void ToggleTask()
        {
            if (SelectedTask == null) return;
            var task = SelectedTask;
            var enable = !task.Enabled;

            if (!Guard(() =>
            {
                task.Enabled = enable;
                // 停用后下次运行时间不再成立，清掉它 —— 否则界面上还显示着"下次运行 09:00"，
                // 用户会以为它照样会跑
                if (!enable) task.NextRunTime = null;
                if (task.Trigger is TimeTrigger tt && !enable) tt.NextRunTime = null;
                task.RefreshView();
                _host.SaveTask(task);
            }, enable ? "启用任务" : "停用任务")) return;

            Reload();
        }

        private void RunNow()
        {
            if (SelectedTask == null) return;
            Guard(() => { _ = _host.RunNowAsync(SelectedTask); }, "立即运行");
        }

        private bool ShowEditor(TaskEditorViewModel vm)
        {
            var editor = new Views.TaskEditorWindow
            {
                Owner = System.Windows.Application.Current.MainWindow,
                DataContext = vm
            };
            return editor.ShowDialog() == true;
        }

        // ================= 辅助窗口 =================

        private void OpenLogWindow()
        {
            if (_logWindow != null) { _logWindow.Activate(); return; }

            _logWindow = new Views.LogWindow
            {
                Owner = System.Windows.Application.Current.MainWindow,
                DataContext = new LogViewModel(_host)
            };
            _logWindow.Closed += (s, e) => _logWindow = null;
            _logWindow.Show(); // 非模态：看日志的同时还能操作主窗口
        }

        /// <summary>打开运行记录窗口；taskId 非空时预置成"只看这个任务"</summary>
        private void OpenRunHistory(string taskId)
        {
            if (_historyWindow != null)
            {
                _historyWindow.Activate();
                ((RunHistoryViewModel)_historyWindow.DataContext).SelectTask(taskId);
                return;
            }

            _historyWindow = new Views.RunHistoryWindow
            {
                Owner = System.Windows.Application.Current.MainWindow,
                DataContext = new RunHistoryViewModel(_host, taskId)
            };
            _historyWindow.Closed += (s, e) => _historyWindow = null;
            _historyWindow.Show();
        }

        private void OpenSettings()
        {
            var vm = new SettingsViewModel();
            var win = new Views.SettingsWindow
            {
                Owner = System.Windows.Application.Current.MainWindow,
                DataContext = vm
            };
            if (win.ShowDialog() != true) return;

            vm.Save();

            // 服务模式下引擎在服务进程里，它不会自己重读设置 —— 通过管道让它 reload
            try { _host.ReloadSettings(); } catch { }

            ApplySettings();
            Reload();
        }

        private void Exit()
        {
            App.ForceClose = true;
            System.Windows.Application.Current.Shutdown();
        }

        // ================= 服务管理 =================

        /// <summary>
        /// 以管理员身份启动自身子进程执行服务管理参数，并把结果反馈给用户。
        ///
        /// 子进程是 WinExe（没有控制台），"成没成"靠退出码传回来，"为什么不成"靠
        /// %ProgramData%\TaskScheduler\service-result.txt 传回来 ——
        /// 只显示一句"失败"的话，用户还得自己去翻日志，等于没反馈。
        /// </summary>
        private void Elevated(string arg, string actionName)
        {
            var resultFile = Paths.ServiceResultFilePath;
            try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { }

            Process proc;
            try
            {
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, arg)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                TsDialog.Show("提权失败：" + ex.Message, "提示",
                    TsDialogButtons.Ok, TsDialogIcon.Warning);
                return;
            }
            if (proc == null) return;

            // 提权子进程活着期间禁掉四个服务项：起服务要几秒到几十秒，
            // 期间连点只会弹出一串 UAC，每个都去抢同一份服务配置。
            _serviceBusy = true;
            _serviceMenuStatus = ComposeServiceMenuStatus();
            OnPropertyChanged(nameof(ServiceMenuStatus));
            NotifyServiceCommands();

            _ = Task.Run(() =>
            {
                int code = -1;
                try
                {
                    if (proc.WaitForExit(180000)) code = proc.ExitCode;
                }
                catch { }

                var detail = ReadServiceResult(resultFile);
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    // 应用正在关停，没地方报结果了。这里只把忙标志放下，
                    // 免得万一窗口还活着、四个菜单项永远灰着。
                    _serviceBusy = false;
                    return;
                }

                dispatcher.BeginInvoke(new Action(() =>
                {
                    _serviceBusy = false;
                    RefreshServiceState();
                    if (code == 0)
                    {
                        // 互斥收口：安装/启动服务成功 = 服务接管执行。本地引擎还在跑就立刻停。
                        // 这里不等 EnforceSingleEngine 的下一轮刷新 —— 服务是本程序刚拉起来的，
                        // Running 即可判定，别让用户在"完成"弹窗之后还留在双跑窗口里。
                        string yieldNote = "";
                        if (_serviceRunning)
                        {
                            var engine = App.Engine;
                            if (engine != null && engine.IsRunning)
                            {
                                try
                                {
                                    engine.Stop();
                                    Log.Write("后台服务已接管执行，本地引擎已自动停止（服务启用了就用服务）");
                                    HostMode = "本地模式（引擎已由后台服务接管，重启程序后完全切换）";
                                    yieldNote = "\n\n本地引擎已自动停止：任务由后台服务接管执行，不会重复运行。"
                                              + "重启本程序后将完全切换为服务模式。";
                                }
                                catch (Exception ex) { Log.Error("停用本地引擎失败：" + ex.Message); }
                            }
                        }

                        var status = ServiceControl.QueryStatus();
                        TsDialog.Show(
                            $"{actionName}完成。\n\n当前服务状态：{(status == null ? "未安装" : StatusText(status.Value))}"
                            + yieldNote
                            + (string.IsNullOrWhiteSpace(detail) ? "" : "\n\n" + detail),
                            actionName, TsDialogButtons.Ok, TsDialogIcon.Info);
                    }
                    else
                    {
                        TsDialog.Show(
                            $"{actionName}失败（退出码 {code}）。\n\n"
                            + (string.IsNullOrWhiteSpace(detail) ? "（未能取回详细原因）" : detail)
                            + $"\n\n完整日志：\n{Paths.LogFilePath}",
                            actionName, TsDialogButtons.Ok, TsDialogIcon.Warning);
                    }
                }));
            });
        }

        /// <summary>读子进程写回的结果（第一行是 OK / FAIL，其余是说明）</summary>
        private static string ReadServiceResult(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                var lines = File.ReadAllLines(path);
                if (lines.Length <= 1) return "";
                return string.Join("\n", lines.Skip(1)).Trim();
            }
            catch { return ""; }
        }
    }
}
