using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TaskScheduler.Engine;

namespace TaskScheduler
{
    /// <summary>本进程内托管引擎（UI 直接调用，无需管道）</summary>
    public class LocalTaskService : ITaskService
    {
        private readonly SchedulerEngine _engine;

        public LocalTaskService(SchedulerEngine engine) => _engine = engine;

        public IReadOnlyList<AutomationTask> ListTasks() => _engine.Tasks;

        public void SaveTask(AutomationTask task)
        {
            var existing = _engine.Tasks.FirstOrDefault(x => x.Id == task.Id);
            if (existing != null) _engine.RemoveTask(existing);
            _engine.AddTask(task);
        }

        public void DeleteTask(AutomationTask task)
        {
            var existing = _engine.Tasks.FirstOrDefault(x => x.Id == task.Id);
            if (existing != null) _engine.RemoveTask(existing);
        }

        public Task RunNowAsync(AutomationTask task) => _engine.RunTaskAsync(task, "手动执行");

        public string GetLogs() => Log.Tail(SettingsService.Current.LogTailLines);

        public bool IsEngineRunning => _engine.IsRunning;

        public void StartEngine() => _engine.Start();

        public void StopEngine() => _engine.Stop();

        public IReadOnlyList<RunRecord> GetRunHistory(string taskId = null, RunOutcome? outcome = null,
                                                      string keyword = null, int limit = 0)
            => _engine.History.Query(taskId, outcome, keyword, limit);

        public void ClearRunHistory(string taskId = null)
        {
            if (string.IsNullOrEmpty(taskId)) _engine.History.Clear();
            else _engine.History.ClearTask(taskId);
        }

        public string ReadLogFile(int maxLines) => Log.ReadTailFromFile(maxLines);

        public void ClearLogFile()
        {
            var err = Log.ClearFile();
            if (err != null) throw new System.InvalidOperationException(err);
        }

        public void ReloadSettings() => SettingsService.Reload();
    }
}
