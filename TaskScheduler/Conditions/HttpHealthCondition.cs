using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TaskScheduler.Conditions
{
    /// <summary>
    /// HTTP 健康检查条件：目标服务返回预期状态码（可选：响应体包含指定文本）时才执行。
    ///
    /// 典型用法：部署任务的执行条件配一个 <c>http://10.1.22.5:8080/health</c>，
    /// 只有网关已经起来了才继续；或者拿它当"上下游就绪探测"，比 ping / 端口探测更能反映业务状态。
    ///
    /// 两个实现上的注意点：
    /// 1. 求值走线程池。条件求值的调用方可能是定时器线程、系统事件回调线程，甚至在手动运行时是 UI 线程；
    ///    在 UI 线程上同步等 HTTP 会直接死锁。这里统一用 Task.Run 把请求甩到线程池再同步等结果。
    /// 2. HttpClient 是静态复用的。每次 new 一个 HttpClient 会因为 TIME_WAIT 状态的连接堆积而耗尽端口，
    ///    这是 .NET 里最经典的坑之一。
    /// </summary>
    public class HttpHealthCondition : ConditionBase
    {
        public override ConditionType Type => ConditionType.HttpHealth;

        /// <summary>要检查的地址，例如 http://127.0.0.1:8080/health</summary>
        private string _url = "";
        public string Url
        {
            get => _url;
            set => SetField(ref _url, value);
        }

        /// <summary>
        /// 期望的状态码。填 0 表示不校验状态码 —— 只要能连上、拿到响应就算通过
        /// （适合"服务活着就行，返回 500 也算活着"的探测）。
        /// </summary>
        private int _expectedStatus = 200;
        public int ExpectedStatus
        {
            get => _expectedStatus;
            set => SetField(ref _expectedStatus, value);
        }

        /// <summary>响应体需要包含的文本（区分大小写）。留空表示不校验</summary>
        private string _bodyContains = "";
        public string BodyContains
        {
            get => _bodyContains;
            set => SetField(ref _bodyContains, value);
        }

        private int _timeoutSeconds = 10;
        public int TimeoutSeconds
        {
            get => _timeoutSeconds;
            set => SetField(ref _timeoutSeconds, value);
        }

        /// <summary>
        /// 是否使用系统代理。默认 false：
        /// 健康检查打的多半是内网地址，走代理轻则慢、重则直接被代理拦成 502。
        /// </summary>
        private bool _useProxy;
        public bool UseProxy
        {
            get => _useProxy;
            set => SetField(ref _useProxy, value);
        }

        /// <summary>单个 HttpClient 实例，按是否用代理分两种（HttpClient 一旦创建就不能再改代理设置）</summary>
        private static readonly HttpClient _direct = CreateClient(useProxy: false);
        private static readonly HttpClient _proxied = CreateClient(useProxy: true);

        private static HttpClient CreateClient(bool useProxy)
        {
            var handler = new HttpClientHandler
            {
                UseProxy = useProxy,
                AllowAutoRedirect = true
            };
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; // 超时自己用 CTS 控制
        }

        protected override bool EvaluateCore()
        {
            var url = (Url ?? "").Trim();
            if (url.Length == 0) return false;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "http://" + url;

            var finalUrl = url;
            return Task.Run(() => Probe(finalUrl)).GetAwaiter().GetResult();
        }

        private bool Probe(string url)
        {
            try
            {
                var client = UseProxy ? _proxied : _direct;
                var timeout = TimeSpan.FromSeconds(TimeoutSeconds <= 0 ? 10 : TimeoutSeconds);

                using (var cts = new CancellationTokenSource(timeout))
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var resp = client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                                        .GetAwaiter().GetResult())
                {
                    if (ExpectedStatus > 0 && (int)resp.StatusCode != ExpectedStatus) return false;

                    var want = (BodyContains ?? "").Trim();
                    if (want.Length == 0) return true;

                    var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                    return body.IndexOf(want, StringComparison.Ordinal) >= 0;
                }
            }
            catch
            {
                // 连不上 / 超时 / DNS 失败 —— 都算条件不满足。
                // 不往上抛：抛出去引擎会记成"执行失败"，而这里只是"上游还没就绪"。
                return false;
            }
        }

        protected override string DescribeCore
        {
            get
            {
                var u = string.IsNullOrWhiteSpace(Url) ? "(未设置地址)" : Url.Trim();
                var s = ExpectedStatus > 0 ? $"返回 {ExpectedStatus}" : "可访问";
                var b = string.IsNullOrWhiteSpace(BodyContains) ? "" : $"，响应包含「{BodyContains.Trim()}」";
                return $"{u} {s}{b}";
            }
        }
    }
}
