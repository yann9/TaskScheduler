using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Newtonsoft.Json;

namespace TaskScheduler.Ipc
{
    /// <summary>
    /// UI 进程使用的管道客户端：每次调用新建一条连接，发送命令并读取响应。
    /// </summary>
    public class PipeClient : IDisposable
    {
        private readonly string _pipeName;

        public PipeClient(string pipeName = PipeServer.PipeName)
        {
            _pipeName = pipeName;
        }

        /// <summary>尝试连接管道，用于探测服务是否在线</summary>
        public bool TryPing(int timeoutMs = 800)
        {
            try { using var c = Connect(timeoutMs); return c != null; }
            catch { return false; }
        }

        private NamedPipeClientStream Connect(int timeoutMs)
        {
            var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(timeoutMs);
            return client;
        }

        public IpcResponse Send(IpcCommand cmd, object payload = null, int timeoutMs = 5000)
        {
            using var client = Connect(timeoutMs);
            var req = new IpcRequest
            {
                Cmd = cmd,
                Payload = payload == null ? null : JsonConvert.SerializeObject(payload, IpcJson.Settings)
            };
            var reqBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(req) + "\n");
            client.Write(reqBytes, 0, reqBytes.Length);
            client.Flush();
            using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
                return new IpcResponse { Ok = false, Error = "空响应" };
            return JsonConvert.DeserializeObject<IpcResponse>(line, IpcJson.Settings);
        }

        public void Dispose() { }
    }
}
