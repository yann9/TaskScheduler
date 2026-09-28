using Newtonsoft.Json;

namespace TaskScheduler.Ipc
{
    /// <summary>UI(托盘进程) 与 后台服务 之间的命令</summary>
    public enum IpcCommand
    {
        Ping,
        ListTasks,
        SaveTask,
        DeleteTask,
        RunNow,
        GetLogs,
        GetStatus,
        StartEngine,
        StopEngine,

        // ===== 运行记录与日志 =====
        // 一律追加在末尾：枚举值会随协议一起被两边使用，插在中间会让新旧版本对不上号。

        /// <summary>按条件取运行记录，载荷 RunHistoryQuery</summary>
        GetRunHistory,

        /// <summary>清空运行记录，载荷为 taskId 字符串（null / 空 = 全部清空）</summary>
        ClearRunHistory,

        /// <summary>从日志文件尾部读 N 行，载荷为 int</summary>
        ReadLogFile,

        /// <summary>界面改了设置 → 让服务端重新加载，使保留条数 / 日志阈值立即生效</summary>
        ReloadSettings,

        /// <summary>删除日志文件（日志窗口的"清空日志文件"）</summary>
        ClearLogFile
    }

    /// <summary>IPC 请求：一行 JSON（命令 + 可选 Payload JSON）</summary>
    public class IpcRequest
    {
        public IpcCommand Cmd { get; set; }
        public string Payload { get; set; }
    }

    /// <summary>IPC 响应</summary>
    public class IpcResponse
    {
        public bool Ok { get; set; }
        public string Data { get; set; }
        public string Error { get; set; }
    }

    internal static class IpcJson
    {
        /// <summary>
        /// 管道上同样启用 $type 多态（要传触发器 / 条件 / 动作的具体类型），
        /// 因此必须挂白名单绑定器：管道客户端可以是本机任意用户，而服务端是 LocalSystem。
        /// </summary>
        public static JsonSerializerSettings Settings => JsonSafety.Wire;
    }
}
