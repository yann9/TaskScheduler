using System;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TaskScheduler.Conditions
{
    /// <summary>
    /// 网络条件：
    /// - Host 为空 => 任意网络可用（NetworkInterface.GetIsNetworkAvailable）；
    /// - Host 非空 => 测试到该主机的 TCP 连通性（默认端口 80，可指定）。
    /// </summary>
    public class NetworkConnectedCondition : ConditionBase
    {
        public override ConditionType Type => ConditionType.NetworkConnected;

        /// <summary>为空表示“任意网络可用”；否则测试到该主机的 TCP 连通</summary>
        private string _host = "";
        public string Host
        {
            get => _host;
            set => SetField(ref _host, value);
        }

        /// <summary>测试端口，0 表示用默认 80</summary>
        private int _port;
        public int Port
        {
            get => _port;
            set => SetField(ref _port, value);
        }

        protected override bool EvaluateCore()
        {
            if (string.IsNullOrWhiteSpace(Host))
                return NetworkInterface.GetIsNetworkAvailable();

            try
            {
                using var client = new TcpClient();
                var ar = client.BeginConnect(Host, Port > 0 ? Port : 80, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(3000))
                    return false;
                client.EndConnect(ar);
                return true;
            }
            catch
            {
                return false;
            }
        }

        protected override string DescribeCore
            => string.IsNullOrWhiteSpace(Host) ? "网络已连接" : $"可连通 {Host}:{(Port > 0 ? Port : 80)}";
    }
}
