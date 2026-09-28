using System.ServiceProcess;
using TaskScheduler.Engine;
using TaskScheduler.Ipc;
using TaskScheduler.Persistence;

namespace TaskScheduler.Service
{
    /// <summary>
    /// Windows 服务：在 session 0 中常驻托管 SchedulerEngine 与管道服务器。
    /// 开机自启（SERVICE_AUTO_START），无需用户登录即可运行无界面任务。
    /// </summary>
    public class AutomationService : ServiceBase
    {
        private SchedulerEngine _engine;
        private PipeServer _pipe;

        public AutomationService()
        {
            ServiceName = ServiceControl.ServiceName;
            CanStop = true;
            CanShutdown = true;
            CanPauseAndContinue = false;
        }

        protected override void OnStart(string[] args)
        {
            // 设置要在日志之前就位：日志的滚动阈值、运行记录的保留条数都从设置里读。
            // 服务端不使用"启动界面时自动启动引擎"这一项 —— 服务的唯一职责就是跑任务，
            // 那一项明确是界面行为。
            SettingsService.Init();
            Log.Init(Engine.Paths.LogFilePath);

            // 节假日数据：缓存先就位，再后台刷新（服务模式下没有界面，同步失败只记日志）
            HolidayProvider.Initialize();

            var repo = new TaskRepository();
            _engine = new SchedulerEngine(new ServiceNotificationService(), null);
            _engine.Load();
            _engine.Start();
            _pipe = new PipeServer(_engine);
            _pipe.Start();
            Log.Write("后台服务已启动");
        }

        protected override void OnStop()
        {
            try { _pipe?.Stop(); _engine?.Stop(); _engine?.Save(); }
            catch { }
            Log.Write("后台服务已停止");
        }
    }
}
