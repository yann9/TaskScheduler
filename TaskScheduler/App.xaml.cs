using System;
using System.ServiceProcess;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using TaskScheduler.Engine;
using TaskScheduler.Ipc;
using TaskScheduler.Native;
using TaskScheduler.Service;
using TaskScheduler.Tray;
using TaskScheduler.ViewModels;

namespace TaskScheduler
{
    public partial class App : System.Windows.Application
    {
        /// <summary>
        /// 主窗口标题。单实例唤醒靠它找窗口（FindWindow），改了 MainWindow.xaml 里的 Title 必须同步改这里。
        /// </summary>
        public const string MainWindowTitle = "自动化任务调度器";

        // 单实例。用 Local\ 而不是 Global\：创建 Global 命名对象需要 SeCreateGlobalPrivilege，
        // 普通交互用户默认没有，会直接抛 AccessDenied。Local\ 覆盖"同一会话内重复双击"这个主场景。
        private const string SingleInstanceMutexName = @"Local\TaskSchedulerUI_Instance";
        private const string ShowWindowEventName = @"Local\TaskSchedulerUI_Show";

        /// <summary>
        /// 优雅退出信号。发这个事件的用途：重新发布 / 升级前请运行中的实例先退出，
        /// 走完 OnExit 的 Stop()+Save()（把最新排期落盘），再强杀 ——
        /// 直接 Stop-Process 会跳过保存，下次启动按存档判断"错过"，可能重复补跑。
        /// </summary>
        private const string ExitEventName = @"Local\TaskSchedulerUI_Exit";

        private Mutex _instanceMutex;
        private EventWaitHandle _showEvent;
        private EventWaitHandle _exitEvent;
        private Thread _showThread;
        private volatile bool _exiting;
        private System.Windows.Interop.HwndSource _mainHwndSource;

        /// <summary>本地模式下的引擎引用（服务模式为 null）</summary>
        public static SchedulerEngine Engine { get; private set; }

        public static TrayIconManager Tray { get; private set; }

        /// <summary>为 true 时允许真正退出（绕过"关闭即最小化到托盘"）</summary>
        public static bool ForceClose { get; set; }

        /// <summary>
        /// 服务模式下的"交互会话事件转发器"宿主引用。
        /// 服务运行在 session 0 收不到锁屏/解锁/远程桌面等交互会话事件，
        /// 由本进程（待在交互会话里）感知后经管道转发给后台服务。
        /// 本地模式不挂接（引擎自己监听，转发会双触发）。
        /// </summary>
        private static ITaskService _sessionHost;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 重置系统"菜单右对齐"（SPI_GETMENUDROPALIGNMENT=1 会导致所有菜单向左镜像弹出）。
            // .NET Framework 下该反射字段真实生效（与 .NET 8 不同），启动时重置一次即可。
            try
            {
                var fi = typeof(System.Windows.SystemParameters).GetField("_menuDropAlignment",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                fi?.SetValue(null, false);
            }
            catch { }

            // UI 线程未处理异常：记日志 + 弹窗，避免静默退出
            DispatcherUnhandledException += (s, ex) =>
            {
                Log.Error("UI 未处理异常: " + ex.Exception);
                Views.TsDialog.Show(
                    "程序运行出错：\n" + ex.Exception?.Message,
                    MainWindowTitle, Views.TsDialogButtons.Ok,
                    Views.TsDialogIcon.Error);
                ex.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                Log.Error("未处理异常: " + ex.ExceptionObject);
            };

            base.OnStartup(e);

            // 设置必须在日志之前就位：Log 的滚动阈值是从设置里读的
            SettingsService.Init();
            var settings = SettingsService.Current;
            Log.Init(Paths.LogFilePath);

            // 单实例：重复启动不只是多一个托盘图标 —— 本地托管模式下两个进程各自建一个引擎、
            // 各自加载同一份 tasks.json，同一批任务会被跑两遍，排期还会互相覆盖。
            if (!AcquireSingleInstance())
            {
                Shutdown();
                return;
            }

            Log.Write("程序启动");

            // 节假日数据：先同步读本地缓存（毫秒级，立即生效），再后台异步刷新。
            // 必须赶在引擎启动之前 —— 引擎一启动就会按"跳过节假日"规则排期，缓存要先就位。
            HolidayProvider.Initialize();

            // 菜单对齐已在上方反射重置；MainWindow 的 OnMenuItemLoaded 作为兜底再强制一次方向。

            // 决定托管方式：能连上后台服务就走服务模式，否则本进程托管
            ITaskService host = null;
            bool serviceMode;
            Log.Write("探测后台服务管道…");
            bool pipeOk;
            using (var probe = new PipeClient())
            {
                // 探测超时不能太短：服务端 PipeServer 是单线程串行处理请求的，
                // 它正在服务别的请求时新连接会排队。600ms 很容易被误判成"服务不在"，
                // 而误判的代价是本地引擎和后台服务的引擎同时跑同一批任务。
                pipeOk = probe.TryPing(3000);

                // 服务已经注册、却连不上 —— 最常见的原因是"刚装完就启动界面"这个竞态：
                // sc start 早就把状态置成 Running 并返回了，但服务的 OnStart 里
                // 管道可能还没建出来（安装器的 [Run] "立即启动"正好会踩到）。
                // 给它两次短重试。服务没装时不做重试：那种情况 TryPing 是立即失败
                // （管道不存在），多等两秒纯属浪费用户时间。
                if (!pipeOk && ServiceControl.QueryStatus() != null)
                {
                    for (int i = 0; i < 2 && !pipeOk; i++)
                    {
                        Thread.Sleep(600);
                        pipeOk = probe.TryPing(1500);
                    }
                }
            }

            if (pipeOk)
            {
                host = new RemoteTaskService();
                serviceMode = true;
                Log.Write("服务模式=true");
            }
            else
            {
                serviceMode = false;

                // 互斥规则（2026-09-24 周工拍板）：服务已安装时执行权一律归服务，
                // 界面进程只在"服务未安装"时才本地托管。服务装了但管道连不上
                //（最常见是服务没在跑），先尝试把它拉起来再探一次管道；
                // 实在拉不起来（多为普通权限没有 SERVICE_START）才临时本地托管，
                // 运行期由 MainViewModel 的定时器持续收敛（服务一 Running 就停本地引擎）。
                var svcStatus = ServiceControl.QueryStatus();
                if (svcStatus == ServiceControllerStatus.Stopped)
                {
                    Log.Write("服务已安装但未运行，尝试自动启动（服务启用了就用服务）…");
                    try
                    {
                        // 只发起不等待：Start() 立即返回；服务真正报 Running 可能要几秒，
                        // 在启动路径上同步等 30 秒（StartSvc 的默认等待）不可接受。
                        using (var sc = new ServiceController(ServiceControl.ServiceName))
                            sc.Start();
                        using (var probe2 = new PipeClient())
                            pipeOk = probe2.TryPing(5000);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("自动启动后台服务未成功（多为权限不足）：" + ex.Message);
                    }
                    if (pipeOk)
                    {
                        host = new RemoteTaskService();
                        serviceMode = true;
                        Log.Write("服务模式=true（已自动启动后台服务）");
                    }
                }

                if (!serviceMode)
                {
                    Log.Write("服务模式=false（本地托管）");
                    WarnIfServiceUnreachable();
                }
            }

            // 托盘启停回调与主窗口的「引擎」菜单走同一套语义：
            //   启动前过双跑守卫（ConfirmLocalEngineStart）；失败要弹得出来，不能点了没反应；
            //   成功后立即把菜单可用状态同步一遍 —— 主窗口的定时刷新要 2~3 秒后才轮到，
            //   用户刚点完托盘就再右键，看到的必须是新状态。
            Tray = new TrayIconManager(
                startEngine: () =>
                {
                    if (!ConfirmLocalEngineStart()) return;
                    try { host?.StartEngine(); }
                    catch (Exception ex)
                    {
                        Log.Error("托盘启动引擎失败：" + ex.Message);
                        Views.TsDialog.Show("启动引擎失败：\n\n" + ex.Message,
                            MainWindowTitle, Views.TsDialogButtons.Ok, Views.TsDialogIcon.Warning);
                    }
                    try { Tray?.UpdateEngineMenu(host != null && host.IsEngineRunning); } catch { }
                },
                stopEngine: () =>
                {
                    try { host?.StopEngine(); }
                    catch (Exception ex)
                    {
                        Log.Error("托盘停止引擎失败：" + ex.Message);
                        Views.TsDialog.Show("停止引擎失败：\n\n" + ex.Message,
                            MainWindowTitle, Views.TsDialogButtons.Ok, Views.TsDialogIcon.Warning);
                    }
                    try { Tray?.UpdateEngineMenu(host != null && host.IsEngineRunning); } catch { }
                },
                exitApp: ExitApp);
            Log.Write("创建托盘图标…");
            Tray.Show();

            if (!serviceMode)
            {
                Log.Write("本地模式：加载并启动调度引擎…");
                var engine = new SchedulerEngine(Tray, System.Windows.Threading.Dispatcher.CurrentDispatcher);
                engine.Load();
                // 是否随界面自动启动引擎由设置决定：关掉的人通常是想自己控制"什么时候开始跑任务"。
                if (settings.AutoStartEngine) engine.Start();
                else Log.Write("设置里关闭了「启动界面时自动启动引擎」，引擎保持停止状态");
                Engine = engine;
                host = new LocalTaskService(engine);
            }
            else
            {
                Log.Write("已连接到后台服务，UI 仅作为配置/监控客户端");
            }

            // 服务模式：本进程在交互会话中，能收到锁屏/解锁/远程桌面/注销等会话事件；
            // 服务在 session 0 收不到，这里挂上转发器，把事件经管道送给服务触发匹配任务。
            if (serviceMode)
            {
                _sessionHost = host;
                try
                {
                    SystemEvents.SessionSwitch += OnUiSessionSwitch;
                    SystemEvents.SessionEnding += OnUiSessionEnding;
                    Log.Write("服务模式：已挂接交互会话事件转发（锁屏/解锁/远程桌面/注销关机）");
                }
                catch (Exception ex) { Log.Error("挂接交互会话事件转发失败：" + ex.Message); }
            }

            Log.Write("创建并显示主窗口…");
            var main = new Views.MainWindow();
            main.DataContext = new MainViewModel(host, serviceMode);

            // 窗口必须先 Show 才有可用的 HWND（消息钩子挂在它上面）
            //
            // 是否最小化只看命令行参数，不读设置文件：
            // --minimized 是写自启注册表项时带上的（见 AutoStartRegistration.Enable），
            // 只有"随 Windows 登录自动启动"那一次才会带上；用户手动双击图标没有参数，
            // 永远正常显示主窗口 —— 否则设了这项之后点图标就"什么都没发生"，会被当成程序坏了。
            if (LaunchedMinimized(e.Args))
            {
                // 用 Opacity=0 过渡，避免"闪一下再收起"
                main.Opacity = 0;
                main.Show();
                main.Hide();
                main.Opacity = 1;
                Log.Write("命令行带 " + AutoStartRegistration.MinimizedArgument + "：启动后直接最小化到托盘");
            }
            else
            {
                main.Show();
            }

            AttachMainWindowHook(main);
            StartShowWindowListener();

            // 任务文件损坏 / 从备份恢复：必须让用户知道，否则他会以为任务"自己没了"
            var loadWarning = serviceMode ? null : Engine?.LastLoadWarning;
            if (!string.IsNullOrEmpty(loadWarning))
            {
                Views.TsDialog.Show(loadWarning, "任务文件异常",
                    Views.TsDialogButtons.Ok, Views.TsDialogIcon.Warning);
            }

            // 服务连不上之类"记录在案、启动收尾再报"的告警，走托盘气泡（非阻塞）
            FlushStartupWarnings();

            Log.Write("启动完成");
        }

        /// <summary>
        /// 本次启动是否应当直接最小化到托盘。
        ///
        /// 只认命令行里的 <see cref="AutoStartRegistration.MinimizedArgument"/>：
        /// 该参数由自启注册表项带入，所以"登录自启"和"手动双击"能区分开。
        /// 大小写不敏感（注册表值可能被手工改过）。
        /// </summary>
        private static bool LaunchedMinimized(string[] args)
        {
            if (args == null) return false;
            foreach (var a in args)
            {
                if (string.Equals(a, AutoStartRegistration.MinimizedArgument,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 单实例保护。已有实例存在时：尽力把它叫出来，本进程退出。
        ///
        /// 关键点是"叫不动"的情况必须能自恢复：以前这里若拿不到命名事件就无条件退出，
        /// 结果是"界面退出以后就再也打不开了"—— 明明没有任何窗口出现，双击图标却毫无反应。
        /// 现在改成：只要找不到任何可以唤起的窗口，就接管启动，宁可承担双实例风险，
        /// 也不能让用户点完图标什么都看不到。
        /// </summary>
        private bool AcquireSingleInstance()
        {
            bool createdNew;
            try
            {
                _instanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
            }
            catch (UnauthorizedAccessException ex)
            {
                // 已有一个"更高权限级别"的实例（典型是以管理员身份启动的），打不开它的互斥体句柄。
                // 这仍然属于"已有实例"，不能当第一个实例直接跑起来。
                Log.Write("单实例互斥体访问被拒（可能有以管理员身份运行的实例）：" + ex.Message);
                createdNew = false;
            }
            catch (Exception ex)
            {
                Log.Error("创建单实例互斥体失败（继续启动）：" + ex.Message);
                return true;
            }

            if (createdNew) return true;

            if (TryWakeExistingInstance())
            {
                Log.Write("检测到已有实例在运行，已请求它显示主窗口；本次启动退出");
                return false;
            }

            Log.Write("互斥体已被占用，但找不到任何可唤起的主窗口（可能是残留进程或跨权限实例）"
                      + "→ 本次启动改为接管运行");
            return true;
        }

        /// <summary>
        /// 三条通道依次尝试把已运行的实例叫到前台。
        /// 顺序是按"可靠性 + 无害程度"排的：事件最干净，广播次之，硬 ShowWindow 有副作用所以放最后。
        /// </summary>
        private static bool TryWakeExistingInstance()
        {
            // 通道 1：命名事件。同版本、同权限级别时最可靠 ——
            // 对方收到后会走完整的 Show / Restore / Activate 流程。
            for (int i = 0; i < 8; i++)
            {
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var ev))
                    {
                        ev.Set();
                        ev.Dispose();
                        return true;
                    }
                }
                catch { }
                Thread.Sleep(200);
            }

            // 通道 2：广播自定义窗口消息（对方用 HwndSource 钩子监听）。
            // 不依赖命名对象，对方窗口已经建好就能收到。
            try
            {
                NativeMethods.BroadcastShowMessage();
                // 广播是异步的，给对方的 dispatcher 一点时间处理
                Thread.Sleep(400);
                if (NativeMethods.TryFindWindow(MainWindowTitle)) return true;
            }
            catch { }

            // 通道 3：直接找窗口并强制显示。
            // 前两条都被拒说明对方在更高的完整性级别上（UIPI 拦下了跨进程消息），
            // 这时硬把窗口显示出来 —— WPF 内部记录的状态可能和实际显示不一致，但总好过没反应。
            for (int i = 0; i < 5; i++)
            {
                if (NativeMethods.TryForceShowWindow(MainWindowTitle)) return true;
                Thread.Sleep(200);
            }

            return false;
        }

        /// <summary>
        /// 手动启动本地引擎前的双跑守卫（主窗口「引擎 → 启动引擎」与托盘菜单共用）。
        ///
        /// 后台服务正在运行时再起本地引擎 = 两个引擎重复执行同一批任务 —— 这是
        /// 程序明确不允许的状态，所以必须在知情的前提下让用户二选一，而不是默默执行。
        /// 返回 false = 用户取消（或判断不了状态时不会走到弹框那一步）。
        /// </summary>
        public static bool ConfirmLocalEngineStart()
        {
            try
            {
                var st = ServiceControl.QueryStatus();
                if (st != ServiceControllerStatus.Running) return true; // 没装/没在跑：不会双跑，直接放行

                var r = Views.TsDialog.Show(
                    "后台服务正在运行，任务已由它执行。\n\n"
                    + "同时启动本地引擎，会让界面和服务两个引擎重复执行同一批任务。\n"
                    + "若要临时使用本地模式，请先在「服务」菜单里停止后台服务。\n\n"
                    + "确定仍要启动本地引擎吗？",
                    MainWindowTitle, Views.TsDialogButtons.YesNo,
                    Views.TsDialogIcon.Warning);
                return r == System.Windows.MessageBoxResult.Yes;
            }
            catch
            {
                return true; // 服务状态判断不了就不拦，别把正常路径堵死
            }
        }

        /// <summary>
        /// 服务已安装却连不上管道时的告警文本（null = 不需要告警）。
        ///
        /// ⚠ 这里只**记录**，不弹模态框。原因有两条，都不是洁癖：
        ///   1) 此时托盘图标和主窗口都还没创建（本方法在 OnStartup 早期调用），
        ///      没有一个合适的所有者窗口，模态框会孤零零地悬在桌面上；
        ///   2) **模态 MessageBox 会把消息循环钉死在自己的嵌套循环里** ——
        ///      安装器/发布脚本发来的"优雅退出"请求虽然能被 dispatcher 收到，
        ///      但 Application.Shutdown() 要等用户点掉对话框才可能走完，
        ///      于是升级安装只能退化成强杀，丢掉内存里的 NextRunTime 存档。
        ///      一个启动告警不该有这种副作用。
        /// 改为启动收尾时用托盘气泡提示（非阻塞），并写进日志。
        /// </summary>
        private static string _startupServiceWarning;

        /// <summary>服务已安装却连不上管道 —— 明确告诉用户，别让他蒙在鼓里</summary>
        private static void WarnIfServiceUnreachable()
        {
            try
            {
                var st = ServiceControl.QueryStatus();
                if (st == null) return; // 没装服务，本地模式就是正常用法

                _startupServiceWarning =
                    "后台服务已安装（当前状态：" + st + "），但界面连不上它的管理管道，已临时使用「本地模式」。"
                    + "程序会持续监视服务状态：服务一旦启动，本地引擎会自动停止、任务由服务接管，"
                    + "两个引擎不会重复执行同一批任务。";
                Log.Error(_startupServiceWarning);
            }
            catch { }
        }

        /// <summary>把启动期间攒下的告警用托盘气泡发出去（非阻塞，托盘和窗口都已就绪时调用）</summary>
        private void FlushStartupWarnings()
        {
            if (string.IsNullOrEmpty(_startupServiceWarning)) return;
            try { Tray?.ShowNotification("无法连接后台服务", _startupServiceWarning); }
            catch (Exception ex) { Log.Error("发送启动告警气泡失败：" + ex.Message); }
        }

        /// <summary>监听"另一个实例被启动"（把窗口唤到前台）与"请求退出"（发布/升级前优雅收尾）</summary>
        private void StartShowWindowListener()
        {
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
                _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
            }
            catch (Exception ex)
            {
                // 事件建不出来不致命：广播消息与 FindWindow 两条通道仍能唤起窗口，
                // 只是 publish.ps1 的"请先优雅退出"这一步会退化（会改为强杀）。
                Log.Error("创建单实例通知事件失败（仍可通过窗口消息唤起）：" + ex.Message);
                return;
            }

            var handles = new WaitHandle[] { _showEvent, _exitEvent };
            _showThread = new Thread(() =>
            {
                while (!_exiting)
                {
                    try
                    {
                        var idx = WaitHandle.WaitAny(handles, 1000);
                        if (idx == 0) Dispatcher.BeginInvoke(new Action(ShowMainWindow));
                        else if (idx == 1)
                        {
                            Log.Write("收到外部退出请求，正在保存并退出…");
                            Dispatcher.BeginInvoke(new Action(ExitApp));
                            return;
                        }
                    }
                    catch { }
                }
            })
            { IsBackground = true, Name = "SingleInstanceShow" };
            _showThread.Start();
        }

        /// <summary>在主窗口的消息循环上挂钩子，用于接收其他实例广播的"显示主窗口"</summary>
        private void AttachMainWindowHook(Window main)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(main).Handle;
                if (hwnd == IntPtr.Zero) return;
                _mainHwndSource = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                _mainHwndSource?.AddHook(OnMainWindowMessage);
            }
            catch (Exception ex)
            {
                Log.Error("注册主窗口消息钩子失败：" + ex.Message);
            }
        }

        private IntPtr OnMainWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0) return IntPtr.Zero;

            // 安装器广播的"请求退出"。与命令行 --exit 走同一条收尾路径（OnExit 里 Stop() + Save()）。
            // 这条通道不依赖"对方进程认识什么开关"，所以哪怕界面是旧版本、或者 exe 已经被
            // 覆盖成新版本，只要窗口还在就不会失联。
            if (msg == NativeMethods.ExitRequestMessageId)
            {
                Log.Write("收到安装器广播的退出请求，正在保存并退出…");
                // 转投到 dispatcher 队列，不在窗口过程里直接 Shutdown
                Dispatcher.BeginInvoke(new Action(ExitApp));
                handled = true;
                return IntPtr.Zero;
            }

            if ((uint)msg == NativeMethods.ShowWindowMessageId)
            {
                ShowMainWindow();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void ShowMainWindow()
        {
            var w = MainWindow;
            if (w == null) return;
            try
            {
                if (!w.IsVisible) w.Show();
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Activate();
                // Activate 常被前台窗口锁挡住，Topmost 抖动一次强制提到最前
                w.Topmost = true;
                w.Topmost = false;
                w.Focus();
            }
            catch { }
        }

        /// <summary>
        /// 请求已运行的实例优雅退出（安装器 / 发布脚本通过命令行 <c>--exit</c> 调用）。
        ///
        /// 为什么必须走命名事件而不是强杀：OnExit 里会 Stop() + Save()，把最新的
        /// NextRunTime 落盘。直接终止进程会让存档停在旧排期上，下次启动可能把
        /// 早已错过的任务当成"漏跑"而重复执行一遍。
        /// </summary>
        /// <returns>true = 找到实例并已发出退出请求；false = 没有实例在跑，或叫不动</returns>
        public static bool RequestExistingInstanceExit()
        {
            try
            {
                if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var ev)) return false;
                using (ev) ev.Set();
                return true;
            }
            catch
            {
                // 对方在更高的完整性级别上（例如界面以管理员身份运行）时打不开句柄。
                // 这里保守返回 false，由调用方决定要不要降级为强杀。
                return false;
            }
        }

        private void ExitApp()
        {
            Tray?.Hide();
            ForceClose = true;
            System.Windows.Application.Current.Shutdown();
        }

        // ================= 交互会话事件转发（服务模式专用） =================

        /// <summary>
        /// 交互会话里的会话切换事件 → 映射成 SystemEventType 后转发给后台服务。
        /// 只关心锁屏 / 解锁 / 远程桌面连接 / 断开这四类（它们都发往交互会话窗口，
        /// 服务在 session 0 收不到）。
        /// </summary>
        private static void OnUiSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            var ev = e.Reason switch
            {
                SessionSwitchReason.SessionLock => SystemEventType.Lock,
                SessionSwitchReason.SessionUnlock => SystemEventType.Unlock,
                SessionSwitchReason.RemoteConnect => SystemEventType.RemoteConnect,
                SessionSwitchReason.RemoteDisconnect => SystemEventType.RemoteDisconnect,
                _ => (SystemEventType?)null
            };
            if (ev.HasValue) ForwardSystemEvent(ev.Value);
        }

        /// <summary>注销 / 关机前：同样只在交互会话里才收得到，转发给服务作为"最后机会"触发</summary>
        private static void OnUiSessionEnding(object sender, SessionEndingEventArgs e)
        {
            ForwardSystemEvent(SystemEventType.SessionEnding);
        }

        private static void ForwardSystemEvent(SystemEventType ev)
        {
            // 失败（服务正好在重启 / 管道短暂不可用）只记日志，不阻塞 UI 消息循环
            try { _sessionHost?.NotifySystemEvent(ev); }
            catch (Exception ex) { Log.Error("转发系统事件失败（" + ev + "）：" + ex.Message); }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _exiting = true;
            try { if (Engine != null) { Engine.Stop(); Engine.Save(); } } catch { }
            try { Tray?.Dispose(); } catch { }
            try { SystemEvents.SessionSwitch -= OnUiSessionSwitch; } catch { }
            try { SystemEvents.SessionEnding -= OnUiSessionEnding; } catch { }
            try { _mainHwndSource?.RemoveHook(OnMainWindowMessage); } catch { }
            try { _showEvent?.Dispose(); } catch { }
            try { _exitEvent?.Dispose(); } catch { }
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            try { _instanceMutex?.Dispose(); } catch { }
            base.OnExit(e);
        }
    }
}
