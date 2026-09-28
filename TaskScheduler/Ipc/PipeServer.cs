using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using TaskScheduler.Engine;
using TaskScheduler.Persistence;

namespace TaskScheduler.Ipc
{
    /// <summary>
    /// 后台服务内的命名管道服务器：接收 UI 进程发来的命令，转发给 SchedulerEngine。
    /// 使用一行一 JSON 的简单文本协议（长度不定，遇换行结束）。
    /// </summary>
    public class PipeServer
    {
        public const string PipeName = "TaskSchedulerSvcPipe";

        /// <summary>管道读写缓冲区。两端都显式给同一组值，避免默认值随运行时版本漂移。</summary>
        private const int PipeBufferSize = 4096;

        /// <summary>
        /// 管道的访问控制表。
        ///
        /// ★ 必须显式指定，不能省。省掉的话 <see cref="NamedPipeServerStream"/> 会用
        /// **创建进程令牌里的默认安全描述符** —— 而服务是以 LocalSystem 在 session 0 创建的，
        /// 那份默认 DACL 里**没有交互登录的用户**。后果：界面进程（普通用户、未提权）
        /// 连接时直接收到 "对路径的访问被拒绝"，探测失败 → 降级成"本地托管模式"，
        /// 并在启动时弹出"无法连接后台服务"警告。
        ///
        /// 实测（Win11 10.0.26200）：管道对象 \\.\pipe\TaskSchedulerSvcPipe 明明存在，
        /// 但以 YANN-NOTEBOOK\zhouy（非提权）连接时报 "对路径的访问被拒绝"。
        ///
        /// 授权范围只到"已通过身份验证的账户"，不碰 Everyone / Anonymous：
        /// 任务数据本来就放在 %ProgramData%\TaskScheduler 下、对 Users 可写，
        /// 所以给 Authenticated Users 读写权限不会额外扩大攻击面。
        /// </summary>
        private static readonly PipeSecurity PipeAcl = CreatePipeAcl();

        private static PipeSecurity CreatePipeAcl()
        {
            var acl = new PipeSecurity();

            // 服务自身
            acl.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));

            // 管理员：便于排障时直接用提权的 PowerShell 连管道看状态
            acl.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));

            // 已登录的用户：界面进程就是它。缺了这一条 = 界面永远连不上服务。
            acl.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));

            return acl;
        }

        private readonly SchedulerEngine _engine;
        private Thread _thread;
        private volatile bool _running;

        public PipeServer(SchedulerEngine engine)
        {
            _engine = engine;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "IpcPipe" };
            _thread.Start();
        }

        public void Stop() => _running = false;

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    // 8 个参数的重载是**唯一**能带 PipeSecurity 的构造（net48 与 .NET Core 都有），
                    // 所以缓冲区大小必须显式给值。
                    using var pipe = new NamedPipeServerStream(
                        PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                        PipeBufferSize, PipeBufferSize, PipeAcl);
                    pipe.WaitForConnection();
                    Handle(pipe);
                }
                catch
                {
                    if (!_running) break;
                    Thread.Sleep(200);
                }
            }
        }

        private void Handle(NamedPipeServerStream pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) return;
                var req = JsonConvert.DeserializeObject<IpcRequest>(line, IpcJson.Settings);
                var resp = Dispatch(req);
                var outBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(resp) + "\n");
                pipe.Write(outBytes, 0, outBytes.Length);
                pipe.Flush();
            }
            catch (Exception ex)
            {
                Log.Error("IPC 处理异常：" + ex.Message);
            }
        }

        private IpcResponse Dispatch(IpcRequest req)
        {
            try
            {
                switch (req.Cmd)
                {
                    case IpcCommand.Ping:
                        return Ok("pong");
                    case IpcCommand.ListTasks:
                        return Ok(JsonConvert.SerializeObject(_engine.Tasks, IpcJson.Settings));
                    case IpcCommand.SaveTask:
                        {
                            var t = JsonConvert.DeserializeObject<AutomationTask>(req.Payload, IpcJson.Settings);
                            SaveTask(t);
                            return Ok("saved");
                        }
                    case IpcCommand.DeleteTask:
                        {
                            var t = JsonConvert.DeserializeObject<AutomationTask>(req.Payload, IpcJson.Settings);
                            var existing = _engine.Tasks.FirstOrDefault(x => x.Id == t.Id);
                            if (existing != null) _engine.RemoveTask(existing);
                            return Ok("deleted");
                        }
                    case IpcCommand.RunNow:
                        {
                            var t = JsonConvert.DeserializeObject<AutomationTask>(req.Payload, IpcJson.Settings);
                            var existing = _engine.Tasks.FirstOrDefault(x => x.Id == t.Id);
                            if (existing != null) _ = _engine.RunTaskAsync(existing, "手动执行(服务)");
                            return Ok("running");
                        }
                    case IpcCommand.GetLogs:
                        return Ok(Log.Tail(SettingsService.Current.LogTailLines));
                    case IpcCommand.GetStatus:
                        return Ok(_engine.IsRunning ? "running" : "stopped");
                    case IpcCommand.StartEngine:
                        _engine.Start();
                        return Ok("started");
                    case IpcCommand.StopEngine:
                        _engine.Stop();
                        return Ok("stopped");
                    case IpcCommand.GetRunHistory:
                        {
                            var q = string.IsNullOrWhiteSpace(req.Payload)
                                ? new RunHistoryQuery()
                                : JsonConvert.DeserializeObject<RunHistoryQuery>(req.Payload, IpcJson.Settings)
                                  ?? new RunHistoryQuery();
                            var list = _engine.History.Query(q.TaskId, q.Outcome, q.Keyword, q.Limit);
                            return Ok(JsonConvert.SerializeObject(list, IpcJson.Settings));
                        }
                    case IpcCommand.ClearRunHistory:
                        {
                            var taskId = string.IsNullOrWhiteSpace(req.Payload)
                                ? null
                                : JsonConvert.DeserializeObject<string>(req.Payload, IpcJson.Settings);
                            if (string.IsNullOrEmpty(taskId)) _engine.History.Clear();
                            else _engine.History.ClearTask(taskId);
                            return Ok("cleared");
                        }
                    case IpcCommand.ReadLogFile:
                        {
                            var lines = 500;
                            if (!string.IsNullOrWhiteSpace(req.Payload))
                                lines = JsonConvert.DeserializeObject<int>(req.Payload, IpcJson.Settings);
                            return Ok(Log.ReadTailFromFile(lines));
                        }
                    case IpcCommand.ReloadSettings:
                        SettingsService.Reload();
                        return Ok("reloaded");
                    case IpcCommand.ClearLogFile:
                        {
                            var err = Log.ClearFile();
                            return err == null ? Ok("cleared") : Fail("删除日志文件失败：" + err);
                        }
                    default:
                        return Fail("未知命令");
                }
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        private void SaveTask(AutomationTask t)
        {
            var existing = _engine.Tasks.FirstOrDefault(x => x.Id == t.Id);
            if (existing != null) _engine.RemoveTask(existing);
            _engine.AddTask(t);
        }

        private static IpcResponse Ok(string data) => new IpcResponse { Ok = true, Data = data };
        private static IpcResponse Fail(string err) => new IpcResponse { Ok = false, Error = err };
    }
}
