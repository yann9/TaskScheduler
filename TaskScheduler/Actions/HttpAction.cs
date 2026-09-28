using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        /// <summary>自定义请求头。不在此处理 Content-Type（那由上面的 ContentType 字段单独控制）。</summary>
        public ObservableCollection<HttpHeader> Headers { get; } = new ObservableCollection<HttpHeader>();

        // 超时统一交给下面的 CancellationTokenSource 控制：
        // HttpClient.Timeout 是硬编码的，和任务里可配置的 TimeoutSeconds 语义冲突
        //（配 300 秒也会在 120 秒被砍断）。静态复用避免每次 new 造成 TIME_WAIT 堆积。
        private static readonly HttpClient Client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        public async Task ExecuteAsync(ActionContext context, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Url))
                throw new InvalidOperationException("未指定 URL");

            using var req = new HttpRequestMessage(new System.Net.Http.HttpMethod(Method.ToString()), Url);

            // 自定义请求头：绝大多数头走 req.Headers；少数内容相关头（如 Content-Type / Content-Length）
            // 必须在 req.Content 上设置，TryAddWithoutValidation 失败时再尝试落到内容头。
            foreach (var h in Headers)
            {
                if (string.IsNullOrWhiteSpace(h.Key)) continue;
                var key = h.Key.Trim();
                var val = h.Value ?? string.Empty;
                if (!req.Headers.TryAddWithoutValidation(key, val) && req.Content != null)
                    req.Content.Headers.TryAddWithoutValidation(key, val);
            }

            if (Method != HttpMethodKind.GET && !string.IsNullOrEmpty(Body))
            {
                req.Content = new StringContent(Body, Encoding.UTF8, ContentType ?? "application/json");
            }

            var seconds = TimeoutSeconds > 0 ? TimeoutSeconds : 30;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));

            HttpResponseMessage resp;
            try
            {
                resp = await Client.SendAsync(req, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"请求在 {seconds}s 内未完成（已超时）");
            }

            // 读取响应体（截断，避免超大响应撑爆运行记录）。
            // 关键：把状态码 + 响应体写进运行记录，否则"成功 / 失败"都只见一句结论，没法排障。
            string bodyText = "";
            try { bodyText = await resp.Content.ReadAsStringAsync(); }
            catch { /* 读不出正文也不影响状态码判定 */ }
            if (bodyText.Length > 2000) bodyText = bodyText.Substring(0, 2000) + "…（响应过长已截断）";

            context.AppendOutput($"HTTP 响应：{(int)resp.StatusCode} {resp.StatusCode}");
            if (!string.IsNullOrEmpty(bodyText))
                context.AppendOutput("响应体：" + bodyText);

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP 请求返回状态码 {(int)resp.StatusCode} {resp.StatusCode}");
        }

        public string Describe() => $"HTTP {Method}：{Url}";
    }

    /// <summary>
    /// HTTP 请求头键值对。
    /// 落在 TaskScheduler.Actions 命名空间下，才能通过 tasks.json 的 SafeSerializationBinder 反序列化白名单。
    /// </summary>
    public class HttpHeader
    {
        public string Key { get; set; }
        public string Value { get; set; }

        public HttpHeader() { }
        public HttpHeader(string key, string value)
        {
            Key = key;
            Value = value;
        }
    }
}
