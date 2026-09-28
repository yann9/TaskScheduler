using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TaskScheduler.Engine;
using TaskScheduler.Ipc;

namespace TaskScheduler
{
    /// <summary>
    /// 通过命名管道连接后台服务进行托管。
    ///
    /// 失败处理原则：
    /// - 写操作（保存 / 删除 / 立即运行 / 启停 / 清空记录）失败必须抛出去 —— 以前一律 catch {} 吞掉，
    ///   用户在界面上操作完完全不知道服务端其实拒绝了。
    /// - 读操作（列表 / 日志 / 状态 / 运行记录）失败只记日志并给默认值 —— 这些每秒都在轮询，
    ///   弹窗会淹没用户。
    /// </summary>
    public class RemoteTaskService : ITaskService
    {
        private readonly PipeClient _client = new PipeClient();

        public IReadOnlyList<AutomationTask> ListTasks()
        {
            try
            {
                var r = _client.Send(IpcCommand.ListTasks);
                if (r == null || !r.Ok)
                    throw new InvalidOperationException(Error(r));
                return JsonConvert.DeserializeObject<List<AutomationTask>>(r.Data, IpcJson.Settings)
                       ?? new List<AutomationTask>();
            }
            catch (Exception ex)
            {
                Log.Error("读取任务列表失败：" + ex.Message);
                return new List<AutomationTask>();
            }
        }

        public void SaveTask(AutomationTask task) => SendOrThrow(IpcCommand.SaveTask, task);

        public void DeleteTask(AutomationTask task) => SendOrThrow(IpcCommand.DeleteTask, task);

        public Task RunNowAsync(AutomationTask task)
        {
            SendOrThrow(IpcCommand.RunNow, task);
            return Task.CompletedTask;
        }

        public string GetLogs()
        {
            try
            {
                var r = _client.Send(IpcCommand.GetLogs);
                return r != null && r.Ok ? r.Data : "";
            }
            catch (Exception ex)
            {
                // 轮询频率高，这里只落日志（引擎侧本来也在记）
                Log.Error("读取日志失败：" + ex.Message);
                return "";
            }
        }

        public bool IsEngineRunning
        {
            get
            {
                try
                {
                    var r = _client.Send(IpcCommand.GetStatus);
                    return r != null && r.Ok && r.Data == "running";
                }
                catch { return false; }
            }
        }

        public void StartEngine() => SendOrThrow(IpcCommand.StartEngine);

        public void StopEngine() => SendOrThrow(IpcCommand.StopEngine);

        public IReadOnlyList<RunRecord> GetRunHistory(string taskId = null, RunOutcome? outcome = null,
                                                      string keyword = null, int limit = 0)
        {
            try
            {
                var q = new RunHistoryQuery { TaskId = taskId, Outcome = outcome, Keyword = keyword, Limit = limit };
                var r = _client.Send(IpcCommand.GetRunHistory, q);
                if (r == null || !r.Ok) throw new InvalidOperationException(Error(r));
                return JsonConvert.DeserializeObject<List<RunRecord>>(r.Data, IpcJson.Settings)
                       ?? new List<RunRecord>();
            }
            catch (Exception ex)
            {
                // 运行记录窗口会定时刷新，失败不弹窗，只落日志
                Log.Error("读取运行记录失败：" + ex.Message);
                return new List<RunRecord>();
            }
        }

        public void ClearRunHistory(string taskId = null) => SendOrThrow(IpcCommand.ClearRunHistory, taskId);

        public string ReadLogFile(int maxLines)
        {
            try
            {
                var r = _client.Send(IpcCommand.ReadLogFile, maxLines);
                return r != null && r.Ok ? (r.Data ?? "") : "";
            }
            catch (Exception ex)
            {
                Log.Error("读取日志文件失败：" + ex.Message);
                return "";
            }
        }

        public void ReloadSettings() => SendOrThrow(IpcCommand.ReloadSettings);

        public void NotifySystemEvent(SystemEventType ev) => SendOrThrow(IpcCommand.SystemEvent, (int)ev);

        public void ClearLogFile() => SendOrThrow(IpcCommand.ClearLogFile);

        private void SendOrThrow(IpcCommand cmd, object payload = null)
        {
            IpcResponse r;
            try
            {
                r = _client.Send(cmd, payload);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("无法连接后台服务：" + ex.Message, ex);
            }

            if (r == null) throw new InvalidOperationException("后台服务返回了空响应");
            if (!r.Ok) throw new InvalidOperationException(Error(r));
        }

        private static string Error(IpcResponse r)
            => r == null || string.IsNullOrWhiteSpace(r.Error) ? "后台服务拒绝了该操作" : r.Error;
    }
}
