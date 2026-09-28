using System;
using System.Net.NetworkInformation;

namespace TaskScheduler.Conditions
{
    /// <summary>
    /// 网络类型条件：当前处于哪种连接上。
    ///
    /// 典型用法：「只在有线连接时才跑」—— 大文件备份 / 镜像同步挂上它，
    /// 笔记本一旦切到 WiFi 就自动不跑，不会把公司的无线网拖垮，也不会烧热点流量。
    ///
    /// 判定口径：只看状态为 Up 的适配器，且排除 Loopback。
    /// VPN（Tunnel）、拨号（Ppp）不算"有线"，避免连了 VPN 就把无线也算成有线。
    /// </summary>
    public class NetworkTypeCondition : ConditionBase
    {
        public override ConditionType Type => ConditionType.NetworkType;

        private NetworkKindType _kind = NetworkKindType.Wired;
        public NetworkKindType Kind
        {
            get => _kind;
            set => SetField(ref _kind, value);
        }

        /// <summary>
        /// 适配器名称 / 描述里需要包含的文本（忽略大小写），留空表示不限制。
        /// 用来精确定位某张网卡或某个 VPN，例如填 "VPN"、"WLAN"、"Intel"。
        /// </summary>
        private string _adapterNameContains = "";
        public string AdapterNameContains
        {
            get => _adapterNameContains;
            set => SetField(ref _adapterNameContains, value);
        }

        protected override bool EvaluateCore()
        {
            var filter = (AdapterNameContains ?? "").Trim();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    var t = ni.NetworkInterfaceType;
                    if (t == NetworkInterfaceType.Loopback) continue;

                    if (filter.Length > 0)
                    {
                        var name = (ni.Name ?? "") + " " + (ni.Description ?? "");
                        if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    }

                    bool isWireless = t == NetworkInterfaceType.Wireless80211;
                    // 隧道（VPN）与拨号（PPPoE）既不算有线也不算无线，避免误判
                    bool isTunnelOrDialup = t == NetworkInterfaceType.Tunnel || t == NetworkInterfaceType.Ppp;

                    switch (Kind)
                    {
                        case NetworkKindType.Wired:
                            if (!isWireless && !isTunnelOrDialup) return true;
                            break;
                        case NetworkKindType.Wireless:
                            if (isWireless) return true;
                            break;
                        default:                      // AnyConnected
                            return true;
                    }
                }
                catch
                {
                    // 单张网卡读属性失败（驱动异常 / 已拔出）不影响其它网卡
                }
            }

            return false;
        }

        protected override string DescribeCore
        {
            get
            {
                string what = Kind switch
                {
                    NetworkKindType.Wired => "有线连接",
                    NetworkKindType.Wireless => "无线连接",
                    _ => "任意已连接网络"
                };
                var f = (AdapterNameContains ?? "").Trim();
                return f.Length == 0 ? $"当前为{what}" : $"当前为{what}（网卡名含 {f}）";
            }
        }
    }
}
