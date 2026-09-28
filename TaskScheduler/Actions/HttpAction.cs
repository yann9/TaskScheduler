using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TaskScheduler.Actions
{
    /// <summary>发送 HTTP 请求</summary>
    public class HttpAction : ObservableModel, IAction
    {
        public ActionType Type => ActionType.HttpRequest;

        private string _url = "";
        public string Url
        {
            get => _url;
            set => Set(ref _url, value);
        }

        private HttpMethodKind _method = HttpMethodKind.GET;
        public HttpMethodKind Method
        {
            get => _method;
            set => Set(ref _method, value);
        }

        private string _body;
        public string Body
        {
            get => _body;
            set => Set(ref _body, value);
        }

        private string _contentType = "application/json";
        public string ContentType
        {
            get => _contentType;
            set => Set(ref _contentType, value);
        }

        private int _timeoutSeconds = 30;
        public int TimeoutSeconds
        {
            get => _timeoutSeconds;
            set => Set(ref _timeoutSeconds, value);
        }

        // 超时统一交给下面的 CancellationTokenSource 控制：
        // HttpClient.Timeout 是硬编码的，和任务里可配置的 TimeoutSeconds 语义冲突
        //（配 300 秒也会在 120 秒被砍断）。静态复用避免每次 new 造成 TIME_WAIT 堆积。
        private static readonly HttpClient Client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        public async Task ExecuteAsync(ActionContext context, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Url))
                throw new InvalidOperationException("未指定 URL");

            using var req = new HttpRequestMessage(new System.Net.Http.HttpMethod(Method.ToString()), Url);
            if (Method != HttpMethodKind.GET && !string.IsNullOrEmpty(Body))
            {
                req.Content = new StringContent(Body, Encoding.UTF8, ContentType ?? "application/json");
            }

            var seconds = TimeoutSeconds > 0 ? TimeoutSeconds : 30;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));

            try
            {
                using var resp = await Client.SendAsync(req, cts.Token);
                resp.EnsureSuccessStatusCode();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"请求在 {seconds}s 内未完成（已超时）");
            }
        }

        public string Describe() => $"HTTP {Method}：{Url}";
    }
}
