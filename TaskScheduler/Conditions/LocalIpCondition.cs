using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace TaskScheduler.Conditions
{
    /// <summary>
    /// 本机 IP 条件：当前任一活动网卡的 IPv4 地址命中期望值时成立。
    /// 用于“只有接在公司网段 / 某个固定 IP 时才执行”的场景（例如内网自动登录脚本）。
    ///
    /// 支持在同一个框里写多个值（逗号或分号分隔），命中任意一个即通过。
    /// 单个值有四种写法：
    ///
    ///   10.1.22.19        精确匹配一个地址
    ///   10.1.22.0/24      网段前缀（CIDR）—— 办公网最常用
    ///   10.1.22.          裸前缀，等价于 10.1.22.0/24（点分的段数 × 8 位）
    ///   10.1.22.*         通配，* 只匹配数字与点
    ///
    /// 为什么要有前缀写法：办公网里 DHCP 分到的地址通常只保证网段固定，
    /// 末位每次续约都可能变；写死一个地址第二天就失配，还得回来改任务。
    /// 另外「IP 变了就重跑内网登录」这种情况，用网段判断也更稳。
    ///
    /// 注意：条件在触发时判定；若任务配置了「条件等待窗口」，不满足时会在窗口内反复重试，
    /// 而不是一次就放弃（DHCP 续约、网卡刚起来这类"等几秒就满足"的情况不会白白错过整个周期）。
    /// </summary>
    public class LocalIpCondition : ConditionBase
    {
        public override ConditionType Type => ConditionType.LocalIp;

        /// <summary>
        /// 期望的本机 IPv4 表达式；多个用逗号/分号分隔，任意命中即通过。
        /// 支持四种写法：精确地址、CIDR 前缀（10.1.22.0/24）、裸前缀（10.1.22.）、通配（10.1.22.*）。
        /// </summary>
        private string _ipAddress = "";
        public string IpAddress
        {
            get => _ipAddress;
            set => SetField(ref _ipAddress, value);
        }

        protected override bool EvaluateCore()
        {
            var expected = ParseExpected(IpAddress);
            if (expected.Count == 0) return false;

            foreach (var ip in GetLocalIPv4())
            {
                foreach (var e in expected)
                {
                    if (Match(ip, e)) return true;
                }
            }
            return false;
        }

        protected override string DescribeCore
            => string.IsNullOrWhiteSpace(IpAddress)
                ? "本机 IP（未设置地址）"
                : $"本机 IP 属于 {IpAddress.Trim()}";

        // ---------- 工具 ----------

        /// <summary>取当前所有“已连接”网卡的 IPv4 地址（排除回环与未启用的网卡）</summary>
        public static List<string> GetLocalIPv4()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        list.Add(ua.Address.ToString());
                    }
                }
            }
            catch
            {
                // 网卡信息不可读时按“没有匹配的地址”处理
            }
            return list;
        }

        /// <summary>把“10.1.22.19, 10.1.22.0/24, 10.1.22.*”拆成列表</summary>
        public static List<string> ParseExpected(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return list;

            foreach (var part in raw.Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var v = part.Trim();
                if (v.Length > 0) list.Add(v);
            }
            return list;
        }

        /// <summary>
        /// 判断某个实际 IPv4 是否命中整段表达式。
        /// 抽成公开方法是为了让编辑器能即时预览"当前本机 IP 是否命中"，
        /// 避免用户填完前缀还要等任务跑到才敢确认。
        /// </summary>
        public static bool Matches(string actualIp, string expression)
        {
            foreach (var p in ParseExpected(expression))
            {
                if (Match(actualIp, p)) return true;
            }
            return false;
        }

        /// <summary>校验单条表达式；合法返回 null，非法返回人话原因（供界面标红提示）</summary>
        public static string Validate(string pattern)
        {
            var p = (pattern ?? "").Trim();
            if (p.Length == 0) return "内容为空";

            // CIDR 前缀
            if (p.IndexOf('/') >= 0)
                return TryParseCidr(p, out _, out _)
                    ? null
                    : "前缀写法应为「网络地址/位数」，例如 10.1.22.0/24（位数取 0–32）";

            // 裸前缀（以点结尾）
            if (p.EndsWith("."))
                return TryParseDotPrefix(p, out _, out _)
                    ? null
                    : "前缀写法应为完整的若干段，例如 10.1.22. 或 192.168.";

            // 通配
            if (p.IndexOf('*') >= 0)
            {
                foreach (var ch in p)
                {
                    if (ch == '*' || ch == '.') continue;
                    if (ch < '0' || ch > '9') return "通配写法里除 * 和 . 之外只能有数字，例如 10.1.22.*";
                }
                return null;
            }

            return TryParseIpv4(p, out _) ? null : "应为 IPv4 地址，例如 10.1.22.19";
        }

        /// <summary>
        /// 把单条表达式翻译成人话（例如 10.1.22.0/24 → “/24 网段 10.1.22.0 – 10.1.22.255”）。
        /// 非法表达式返回 null。
        /// </summary>
        public static string Explain(string pattern)
        {
            var p = (pattern ?? "").Trim();
            if (p.Length == 0 || Validate(p) != null) return null;

            if (p.IndexOf('/') >= 0)
            {
                TryParseCidr(p, out var net, out var len);
                return ExplainNetwork(net, len);
            }
            if (p.EndsWith("."))
            {
                TryParseDotPrefix(p, out var net, out var len);
                return ExplainNetwork(net, len);
            }
            if (p.IndexOf('*') >= 0) return "通配匹配（* 代表任意数字或点）";
            return "精确匹配";
        }

        // ---------- 内部实现 ----------

        /// <summary>匹配单条表达式（不含多值拆分）</summary>
        private static bool Match(string actual, string pattern)
        {
            var p = (pattern ?? "").Trim();
            if (p.Length == 0) return false;

            // CIDR 前缀优先：含 '/' 时不可能是别的写法
            if (p.IndexOf('/') >= 0)
                return TryParseCidr(p, out var net, out var len) && IsInNetwork(actual, net, len);

            // 裸前缀（10.1.22.）
            if (p.EndsWith("."))
                return TryParseDotPrefix(p, out var dnet, out var dlen) && IsInNetwork(actual, dnet, dlen);

            // 含 * 时按通配匹配（* 只允许匹配数字与点，避免误判）
            if (p.IndexOf('*') >= 0)
            {
                var rx = "^" + Regex.Escape(p).Replace(@"\*", @"[0-9.]*") + "$";
                return Regex.IsMatch(actual, rx, RegexOptions.IgnoreCase);
            }

            // 精确
            return string.Equals(actual, p, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>解析 "10.1.22.0/24"</summary>
        private static bool TryParseCidr(string s, out uint network, out int prefixLen)
        {
            network = 0;
            prefixLen = 0;

            var i = s.IndexOf('/');
            if (i <= 0 || i == s.Length - 1) return false;

            var addrPart = s.Substring(0, i).Trim();
            var lenPart = s.Substring(i + 1).Trim();

            if (!int.TryParse(lenPart, out var len) || len < 0 || len > 32) return false;
            if (!TryParseIpv4(addrPart, out network)) return false;

            prefixLen = len;
            return true;
        }

        /// <summary>解析裸前缀 "10.1.22." → 网络号 10.1.22.0，前缀长度 24</summary>
        private static bool TryParseDotPrefix(string s, out uint network, out int prefixLen)
        {
            network = 0;
            prefixLen = 0;

            var body = s.TrimEnd('.');
            if (body.Length == 0) return false;

            var parts = body.Split('.');
            // 已经写满 4 段就没必要用点结尾的写法（那是完整地址，走到这就说明用户多敲了个点）
            if (parts.Length == 0 || parts.Length > 3) return false;

            uint acc = 0;
            foreach (var part in parts)
            {
                if (!TryParseOctet(part, out var b)) return false;
                acc = (acc << 8) | b;
            }
            for (int i = parts.Length; i < 4; i++) acc <<= 8;   // 后面几段补 0

            network = acc;
            prefixLen = parts.Length * 8;
            return true;
        }

        /// <summary>严格解析点分十进制 IPv4（不接受 "10.1.22" 这类缩写，避免和前缀写法混淆）</summary>
        private static bool TryParseIpv4(string s, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            var parts = s.Trim().Split('.');
            if (parts.Length != 4) return false;

            uint acc = 0;
            foreach (var part in parts)
            {
                if (!TryParseOctet(part, out var b)) return false;
                acc = (acc << 8) | b;
            }
            value = acc;
            return true;
        }

        private static bool TryParseOctet(string s, out uint b)
        {
            b = 0;
            if (string.IsNullOrEmpty(s) || s.Length > 3) return false;
            foreach (var ch in s)
            {
                if (ch < '0' || ch > '9') return false;
            }
            return uint.TryParse(s, out b) && b <= 255;
        }

        /// <summary>判断 IPv4 是否落在 网络号/前缀长度 里</summary>
        private static bool IsInNetwork(string ip, uint network, int prefixLen)
        {
            if (!TryParseIpv4(ip, out var v)) return false;

            if (prefixLen <= 0) return true;                  // /0 = 任意地址
            if (prefixLen >= 32) return v == network;         // /32 = 单个地址

            // 上面两个分支挡掉了移位越界的两种情况（<< 32 与 << 0 在 C# 里会被掩码成 << 0）
            var mask = 0xFFFFFFFFu << (32 - prefixLen);
            return (v & mask) == (network & mask);
        }

        private static string ExplainNetwork(uint network, int prefixLen)
        {
            if (prefixLen >= 32) return $"单个地址 {ToIp(network)}";
            if (prefixLen <= 0) return "/0 任意地址";

            var mask = 0xFFFFFFFFu << (32 - prefixLen);
            var first = network & mask;
            var last = first | ~mask;
            return $"/{prefixLen} 网段（{ToIp(first)} – {ToIp(last)}）";
        }

        private static string ToIp(uint v)
            => ((v >> 24) & 0xFF) + "." + ((v >> 16) & 0xFF) + "." + ((v >> 8) & 0xFF) + "." + (v & 0xFF);
    }
}
