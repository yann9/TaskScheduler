using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TaskScheduler.Engine
{
    /// <summary>
    /// 应用内检测更新：拉取版本清单 → 比较版本 → 下载安装包 → 启动安装。
    ///
    /// 【国内网络可达性设计】
    /// GitHub 的 api.github.com / github.com 在国内经常连不上，安装包直链更是几乎不可用；
    /// 早期版本只请求一个固定地址、且写死不走系统代理，等于把这类用户全部挡在门外。现在：
    /// 1) 检查时先发「国内镜像清单」（Gitee 制品仓库里本项目目录下的固定 JSON），
    ///    GitHub 系线路延后 600ms 发起：国内用户通常 0.1 秒就命中镜像，根本不用等 GitHub 超时；
    ///    镜像 404 / 不可达时，GitHub 直连与加速节点自动接手，仍是并发竞速、谁先返回合法清单用谁。
    /// 2) 默认跟随系统代理（代理软件用户直接可用），可在设置里关掉。
    /// 3) 下载安装包时按「清单主地址 → 备用地址 → 检查命中的节点 → 直连 → 其余节点」逐个尝试，
    ///    每个节点只给 12 秒连接时间，卡住就自动换下一条线路，不会一直转圈。
    /// 4) 加速节点是公益服务、随时可能失效，所以内置列表可在「设置 → 更新」里整个替换。
    ///
    /// 【清单来源】
    /// 1) 国内镜像：默认 https://gitee.com/yann9/dist/raw/master/TaskScheduler/update.json 。
    ///    yann9/dist 是**多项目共用**的制品库，按项目分子目录，所以只读本项目目录下的固定清单，
    ///    不用仓库级 releases/latest（那会指向别的项目最新发的 Release，tag 也会跨项目撞名）。
    /// 2) GitHub Releases latest：镜像不可用时的回退，配合加速节点竞速。
    /// 3) 自定义清单：设置里填了地址就以它为准，用于私有发布源 / 内网 / 自建站点。
    ///
    /// 清单字段（大小写不敏感，镜像与自定义清单同格式）：
    /// <code>
    /// {
    ///   "version": "1.2.0",
    ///   "title": "1.2.0 更新",
    ///   "notes": "更新说明，支持多行",
    ///   "url": "https://gitee.com/yann9/dist/raw/master/TaskScheduler/TaskScheduler-setup.exe",
    ///   "fallbackUrl": "https://github.com/yann9/TaskScheduler/releases/download/v1.2.0/...",
    ///   "pageUrl": "https://gitee.com/yann9/dist/tree/master/TaskScheduler",
    ///   "fileName": "TaskScheduler-1.2.0-setup.exe",
    ///   "publishedAt": "2026-11-06T10:00:00Z"
    /// }
    /// </code>
    /// version 必填；url 留空时只提供「打开下载页」；fallbackUrl 在主地址失败时自动接手，
    /// 若属于 GitHub 域名还会自动套用加速节点。Gitee 的 raw 对较大的 .exe 可能返回 403，
    /// 因此发布流水线总会同时给出 GitHub 备用地址，下载不会因此断掉。
    ///
    /// 所有异常都转成结果对象，绝不因网络问题影响主流程。
    /// </summary>
    public static class UpdateService
    {
        /// <summary>默认版本来源：GitHub Releases 最新发布</summary>
        public const string GitHubLatestReleaseApi =
            "https://api.github.com/repos/yann9/TaskScheduler/releases/latest";

        /// <summary>发布页（没有可用安装包时的兜底入口）</summary>
        public const string ReleasesPage = "https://github.com/yann9/TaskScheduler/releases";

        /// <summary>
        /// 内置的 GitHub 加速节点（公益服务，按实测可靠性排序）。
        /// 用法是把原始 GitHub 链接整条拼在前缀后面，例如
        /// https://gh-proxy.com/https://api.github.com/repos/.../releases/latest 。
        ///
        /// 2026-11-06 实测（请求 GitHub releases/latest API）：
        /// gh-proxy.com / gh-proxy.org 返回 200 且内容正确；ghproxy.net 403、
        /// github.akams.cn 404（后者对 Releases 文件直链仍可能有用，留作下载兜底）。
        /// 节点状态随时会变，因此只依赖「至少一个可用」，解析前还会校验返回的确实是 JSON；
        /// 全部失效时也可在「设置 → 更新 → GitHub 加速节点」里整组替换。
        /// </summary>
        public static readonly string[] DefaultProxyPrefixes =
        {
            "https://gh-proxy.com/",
            "https://gh-proxy.org/",
            "https://ghproxy.net/",
            "https://github.akams.cn/"
        };

        /// <summary>单个清单请求的超时（竞速，不必给太久）</summary>
        private const int CheckTimeoutSeconds = 8;

        /// <summary>
        /// 国内镜像清单的「先跑时间」：GitHub 系线路延后这么久再发请求。
        /// 镜像通常 0.1~0.3 秒就能命中，命中后其余请求会被统一取消；
        /// 镜像 404 / 超时时也只多等 0.6 秒，GitHub 竞速随即接手。
        /// </summary>
        private const int MirrorHeadStartMs = 600;

        /// <summary>下载时每条线路的「连上」超时；连上后不再受此限制</summary>
        private const int ConnectTimeoutSeconds = 12;

        private const int DownloadTimeoutMinutes = 30;

        /// <summary>界面里传进来的设置副本优先（设置窗口点「立即检查」时用户可能刚改完还没点确定）</summary>
        private static AppSettings Effective(AppSettings settings)
            => settings ?? SettingsService.Current;

        private static HttpClient CreateClient(TimeSpan timeout, AppSettings settings)
        {
            var handler = new HttpClientHandler
            {
                UseProxy = Effective(settings).UpdateUseSystemProxy,
                Proxy = null, // null = 使用系统默认代理（IE / 代理软件设置的那个）
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler) { Timeout = timeout };
            try { client.DefaultRequestHeaders.UserAgent.ParseAdd("TaskScheduler/" + AppVersion.Current); }
            catch { }
            try { client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json"); }
            catch { }
            return client;
        }

        /// <summary>一条候选线路：地址、给用户看的来源名、命中的加速前缀、发起前的延迟</summary>
        private sealed class Candidate
        {
            public string Url;
            public string Source;
            public string Prefix;

            /// <summary>延后发起的毫秒数，用于让国内镜像先跑</summary>
            public int DelayMs;
        }

        private sealed class FetchOutcome
        {
            public Candidate Candidate;
            public string Json;
            public string Error;
        }

        /// <summary>
        /// 检查更新。多路竞速，任一线路返回合法清单即结束；全部失败才返回失败原因。
        /// 返回的结果对象不会抛异常 —— 启动时自动检查可以静默失败，手动检查则把原因显示出来。
        /// </summary>
        public static async Task<UpdateCheckResult> CheckAsync(
            AppSettings settings = null, CancellationToken ct = default(CancellationToken))
        {
            var cfg = Effective(settings);
            var candidates = BuildCheckCandidates(cfg);

            var bad = candidates.FirstOrDefault(c => !IsHttpUrl(c.Url));
            if (bad != null)
                return UpdateCheckResult.Failed("版本清单地址无效，请填写以 http:// 或 https:// 开头的完整 URL：" + bad.Url,
                                               bad.Source);

            using (var http = CreateClient(TimeSpan.FromSeconds(CheckTimeoutSeconds), cfg))
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var pending = candidates.Select(c => FetchAsync(http, c, cts.Token)).ToList();
                string lastError = "所有版本源都不可达";

                while (pending.Count > 0)
                {
                    var done = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(done);

                    FetchOutcome outcome;
                    try { outcome = await done.ConfigureAwait(false); }
                    catch (Exception ex) { lastError = ex.Message; continue; }
                    if (outcome == null) continue;

                    if (outcome.Json == null)
                    {
                        if (!string.IsNullOrWhiteSpace(outcome.Error)) lastError = outcome.Error;
                        continue;
                    }

                    UpdateInfo info;
                    try { info = ParseManifest(outcome.Json); }
                    catch (Exception ex) { lastError = outcome.Candidate.Source + "：清单解析失败 - " + ex.Message; continue; }

                    if (string.IsNullOrWhiteSpace(info.Version))
                    {
                        lastError = outcome.Candidate.Source + "：清单里没有版本号（version / tag_name）";
                        continue;
                    }

                    // 记录下来：下载安装包时优先复用同一条走通的线路。
                    info.SourcePrefix = outcome.Candidate.Prefix;
                    cts.Cancel(); // 其余请求没必要继续占用公益节点的带宽

                    if (!AppVersion.IsNewer(info.Version, AppVersion.Current))
                        return UpdateCheckResult.UpToDate(outcome.Candidate.Source);
                    return UpdateCheckResult.Newer(info, outcome.Candidate.Source);
                }

                if (ct.IsCancellationRequested)
                    return UpdateCheckResult.Failed("检查已取消", "多线路检查");

                Log.Write("检查更新失败：" + lastError, "WARN");
                return UpdateCheckResult.Failed(
                    lastError + "。可在「设置 → 更新」里改用可访问的版本清单地址，或检查系统代理设置。",
                    "多线路检查（国内镜像 + GitHub 直连 + 加速节点）");
            }
        }

        /// <summary>单条线路的清单请求：把任何异常都收进结果对象，竞速逻辑才不会被一个节点带崩</summary>
        private static async Task<FetchOutcome> FetchAsync(HttpClient http, Candidate c, CancellationToken ct)
        {
            try
            {
                // 让国内镜像先跑：延迟期间若镜像已命中，ct 会被取消，这里抛出
                // OperationCanceledException 并走下面的「别的线路赢了」分支，不记为错误。
                if (c.DelayMs > 0)
                    await Task.Delay(c.DelayMs, ct).ConfigureAwait(false);

                using (var resp = await http.GetAsync(c.Url, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                        return new FetchOutcome
                        {
                            Candidate = c,
                            Error = c.Source + "：HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase
                        };

                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json))
                        return new FetchOutcome { Candidate = c, Error = c.Source + "：内容为空" };

                    // 加速节点失效时常见的是返回一个 HTML 首页 / 验证页，别让它冒充清单。
                    var head = json.TrimStart();
                    if (head.Length == 0 || (head[0] != '{' && head[0] != '['))
                        return new FetchOutcome { Candidate = c, Error = c.Source + "：返回的不是 JSON（节点可能已失效）" };

                    return new FetchOutcome { Candidate = c, Json = json };
                }
            }
            catch (OperationCanceledException)
            {
                // HttpClient.Timeout 到期同样抛 OperationCanceledException，但它用的是内部 token，
                // 传进来的 ct 并没有被取消 —— 这时必须报出「超时」，
                // 否则失败原因会退化成一句没用的「所有版本源都不可达」。
                if (!ct.IsCancellationRequested)
                    return new FetchOutcome { Candidate = c, Error = c.Source + "：连接超时或网络不可达" };
                // 别的线路已经赢了，或用户主动取消；不算错误
                return new FetchOutcome { Candidate = c };
            }
            catch (Exception ex)
            {
                var msg = ex.InnerException?.Message ?? ex.Message;
                return new FetchOutcome { Candidate = c, Error = c.Source + "：" + msg };
            }
        }

        /// <summary>构造本次检查要并发尝试的全部线路</summary>
        private static List<Candidate> BuildCheckCandidates(AppSettings cfg)
        {
            var list = new List<Candidate>();
            var prefixes = ResolveProxyPrefixes(cfg);

            // 用户显式指定的地址独占：私有源 / 内网就该以它为准，不再掺入公开镜像，
            // 免得内网友好地址被外网的旧清单抢先命中。
            var custom = (cfg.UpdateManifestUrl ?? "").Trim();
            if (custom.Length > 0)
            {
                list.Add(new Candidate { Url = custom, Source = "自定义清单（" + HostOf(custom) + "）", Prefix = "" });
                if (IsGitHubUrl(custom))
                {
                    foreach (var p in prefixes)
                        list.Add(new Candidate { Url = p + custom, Source = "自定义清单（" + HostOf(p) + " 加速）", Prefix = p });
                }
                return Dedupe(list);
            }

            // 国内镜像清单：Gitee 制品仓库里本项目目录下的固定文件，直连、无第三方节点。
            // 它不参与"加速"——加速前缀只对 GitHub 域名有意义。
            var mirror = (cfg.UpdateMirrorManifestUrl ?? "").Trim();
            var delay = mirror.Length > 0 ? MirrorHeadStartMs : 0;
            if (mirror.Length > 0)
                list.Add(new Candidate
                {
                    Url = mirror,
                    Source = "国内镜像清单（" + HostOf(mirror) + "）",
                    Prefix = ""
                });

            list.Add(new Candidate
            {
                Url = GitHubLatestReleaseApi,
                Source = "GitHub Releases（直连）",
                Prefix = "",
                DelayMs = delay
            });
            foreach (var p in prefixes)
            {
                list.Add(new Candidate
                {
                    Url = p + GitHubLatestReleaseApi,
                    Source = "GitHub Releases（" + HostOf(p) + " 加速）",
                    Prefix = p,
                    DelayMs = delay
                });
            }
            return Dedupe(list);
        }

        /// <summary>
        /// 解析用户填的加速节点：留空 = 内置列表；direct = 只直连；
        /// 其余按分号 / 逗号 / 换行拆成若干前缀，允许省略 https://。
        /// </summary>
        private static string[] ResolveProxyPrefixes(AppSettings cfg)
        {
            var raw = (cfg.UpdateProxyPrefixes ?? "").Trim();
            if (raw.Length == 0) return DefaultProxyPrefixes;
            if (raw.Equals("direct", StringComparison.OrdinalIgnoreCase)
                || raw == "-" || raw == "0" || raw.Equals("none", StringComparison.OrdinalIgnoreCase))
                return new string[0];

            return raw.Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(x => x.Trim())
                      .Where(x => x.Length > 0)
                      .Select(x => x.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                   || x.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                      ? x : "https://" + x)
                      .Select(x => x.EndsWith("/", StringComparison.Ordinal) ? x : x + "/")
                      .Where(IsHttpUrl)
                      .Distinct()
                      .Take(6)
                      .ToArray();
        }

        private static List<Candidate> Dedupe(List<Candidate> list)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return list.Where(c => seen.Add(c.Url)).ToList();
        }

        /// <summary>把 GitHub API 响应或自定义清单转成统一的 UpdateInfo（按内容自动识别格式）</summary>
        private static UpdateInfo ParseManifest(string json)
        {
            var j = JObject.Parse(json);
            var info = new UpdateInfo();
            var looksGitHub = Str(j, "tag_name") != "" || Get(j, "assets") is JArray;

            if (looksGitHub)
            {
                // ===== GitHub Releases latest =====
                info.Version = Str(j, "tag_name", "name");
                info.Title = Str(j, "name");
                info.Notes = Str(j, "body");
                info.PageUrl = Str(j, "html_url");
                // Gitee 的 Release 用 created_at 而不是 published_at；万一以后把清单
                // 指到 Gitee 的 latest 接口，发布时间也不会空着。
                info.PublishedAt = ParseDate(Str(j, "published_at", "created_at"));

                var assets = Get(j, "assets") as JArray;
                if (assets != null)
                {
                    var all = assets.OfType<JObject>().ToList();
                    // 优先选发布工作流产出的 -setup.exe；portable.zip 是绿色版，不用于就地升级。
                    var setup = all.FirstOrDefault(a =>
                        Str(a, "name").EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                        ?? all.FirstOrDefault(a =>
                        Str(a, "name").EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

                    if (setup != null)
                    {
                        info.DownloadUrl = Str(setup, "browser_download_url", "url");
                        info.FileName = Str(setup, "name");
                    }
                }
            }
            else
            {
                // ===== 自定义清单 =====
                info.Version = Str(j, "version");
                info.Title = Str(j, "title", "name");
                info.Notes = Str(j, "notes", "releaseNotes", "body");
                info.DownloadUrl = Str(j, "url", "downloadUrl", "download_url");
                info.FallbackDownloadUrl = Str(j, "fallbackUrl", "fallback_url", "backupUrl");
                info.PageUrl = Str(j, "pageUrl", "page_url", "html_url");
                info.FileName = Str(j, "fileName", "file_name");
                info.PublishedAt = ParseDate(Str(j, "publishedAt", "published_at", "date", "created_at"));
            }

            if (string.IsNullOrWhiteSpace(info.PageUrl)) info.PageUrl = ReleasesPage;
            if (string.IsNullOrWhiteSpace(info.FileName) && !string.IsNullOrWhiteSpace(info.DownloadUrl))
                info.FileName = FileNameFromUrl(info.DownloadUrl);
            if (string.IsNullOrWhiteSpace(info.Title)) info.Title = "版本 " + AppVersion.Normalize(info.Version);

            return info;
        }

        /// <summary>
        /// 下载安装包到临时目录，按线路顺序自动回退。进度按 Content-Length 报 0–100，
        /// 没有长度时只报 0（界面显示"下载中"）。
        /// 下载用独立的长超时 HttpClient：检查更新那个 8 秒超时的客户端会把大文件下载掐断。
        /// </summary>
        public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int> progress,
                                                       AppSettings settings = null,
                                                       CancellationToken ct = default(CancellationToken))
        {
            if (info == null || !info.CanInstall)
                throw new InvalidOperationException("该版本没有可用的安装包，请打开下载页手动下载。");

            var cfg = Effective(settings);
            var candidates = BuildDownloadCandidates(info, cfg);
            if (candidates.Count == 0)
                throw new InvalidOperationException("下载地址无效：" + info.DownloadUrl);

            var dir = Path.Combine(Path.GetTempPath(), "TaskScheduler-updates");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, SafeFileName(info.FileName));
            var tmp = path + ".part";

            Exception last = null;
            foreach (var c in candidates)
            {
                try
                {
                    Log.Write("下载更新，尝试线路：" + c.Source);
                    return await DownloadOneAsync(c.Url, path, tmp, progress, cfg, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // 用户主动取消，不要继续换线路
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Write("线路 " + c.Source + " 下载失败：" + ex.Message, "WARN");
                    TryDelete(tmp);
                    progress?.Report(0);
                }
            }

            throw new InvalidOperationException(
                "所有下载线路都失败：" + (last?.Message ?? "未知错误") + "。可点「打开下载页」手动下载安装。");
        }

        private static async Task<string> DownloadOneAsync(string url, string path, string tmp,
                                                           IProgress<int> progress, AppSettings cfg,
                                                           CancellationToken ct)
        {
            using (var client = CreateClient(TimeSpan.FromMinutes(DownloadTimeoutMinutes), cfg))
            using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(ConnectTimeoutSeconds)))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connectCts.Token))
            using (var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                                         .ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"服务器返回 {(int)resp.StatusCode} {resp.ReasonPhrase}");

                // 加速节点返回验证页 / 首页时别把 HTML 当 exe 存下来
                var media = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (media.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new InvalidOperationException("返回的网页而不是安装包（节点可能已失效）");

                // 响应头已拿到，取消"连接阶段"的定时，后续慢慢传文件不再被 12 秒限制
                try { connectCts.CancelAfter(Timeout.InfiniteTimeSpan); }
                catch (ObjectDisposedException) { }

                var total = resp.Content.Headers.ContentLength ?? 0L;
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long read = 0;
                    int n;
                    while ((n = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                        read += n;
                        if (total > 0) progress?.Report((int)Math.Min(100L, read * 100L / total));
                    }
                }
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            progress?.Report(100);
            return path;
        }

        /// <summary>
        /// 下载线路：清单主地址 → 备用地址（fallbackUrl）→ 检查命中的节点 → 直连 → 其余节点。
        /// 只有 GitHub 域名的链接才套加速前缀；Gitee / 自建站点等国内地址保持原样直连。
        /// Gitee raw 对较大的 .exe 可能 403，有备用地址时会自动换线路，用户无感。
        /// </summary>
        private static List<Candidate> BuildDownloadCandidates(UpdateInfo info, AppSettings cfg)
        {
            var list = new List<Candidate>();
            AddDownloadCandidates(list, info.DownloadUrl, cfg, info.SourcePrefix, true);
            AddDownloadCandidates(list, info.FallbackDownloadUrl, cfg, info.SourcePrefix, false);
            return Dedupe(list);
        }

        private static void AddDownloadCandidates(List<Candidate> list, string raw,
                                                  AppSettings cfg, string hit, bool primary)
        {
            var url = (raw ?? "").Trim();
            if (!IsHttpUrl(url)) return;

            if (!IsGitHubUrl(url))
            {
                list.Add(new Candidate
                {
                    Url = url,
                    Source = (primary ? "清单地址" : "备用地址") + "（" + HostOf(url) + "）",
                    Prefix = ""
                });
                return;
            }

            hit = (hit ?? "").Trim();
            var prefixes = ResolveProxyPrefixes(cfg);

            // 备用线路通常没有「检查时走通的节点」可复用（检查命中的是国内镜像）。
            // 这时把加速节点排在直连前面：国内直连 GitHub 往往要等满 12 秒超时
            // 才轮到下一条，先试节点能省掉这段无谓等待。
            if (!primary && hit.Length == 0)
            {
                foreach (var p in prefixes)
                    if (IsHttpUrl(p + url))
                        list.Add(new Candidate { Url = p + url, Source = HostOf(p) + " 加速（备用）", Prefix = p });
                list.Add(new Candidate { Url = url, Source = "GitHub 直连（备用）", Prefix = "" });
                return;
            }

            if (hit.Length > 0 && IsHttpUrl(hit + url))
                list.Add(new Candidate { Url = hit + url, Source = "检查命中的节点（" + HostOf(hit) + "）", Prefix = hit });

            list.Add(new Candidate { Url = url, Source = "GitHub 直连", Prefix = "" });
            foreach (var p in prefixes)
            {
                if (p == hit || !IsHttpUrl(p + url)) continue;
                list.Add(new Candidate { Url = p + url, Source = HostOf(p) + " 加速", Prefix = p });
            }
        }

        /// <summary>
        /// 启动下载好的 NSIS 安装包。安装包自己会请求旧实例优雅退出（--exit）并覆盖安装，
        /// 调用方随后主动关闭本程序，避免文件被占用。
        /// 用 UseShellExecute=true：NSIS 清单里已声明 admin，系统会自动弹 UAC。
        /// </summary>
        public static void LaunchInstaller(string installerPath)
        {
            if (!File.Exists(installerPath)) throw new FileNotFoundException("安装包不存在", installerPath);
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        }

        /// <summary>
        /// 打开发布页（下载失败 / 该版本没有安装包时的兜底）。
        /// 检查阶段是靠加速节点才拿到清单的，说明直连大概率打不开，发布页也走同一个节点。
        /// </summary>
        public static void OpenDownloadPage(UpdateInfo info, AppSettings settings = null)
        {
            var url = info?.PageUrl;
            if (!IsHttpUrl(url)) url = ReleasesPage;

            if (IsGitHubUrl(url) && !string.IsNullOrWhiteSpace(info?.SourcePrefix)
                && IsHttpUrl(info.SourcePrefix + url))
                url = info.SourcePrefix + url;

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        // ================= 小工具 =================

        private static JToken Get(JObject o, string name)
            => o.GetValue(name, StringComparison.OrdinalIgnoreCase);

        /// <summary>按多个候选名取值，兼容 GitHub 下划线风格与自定义清单的驼峰风格</summary>
        private static string Str(JObject o, params string[] names)
        {
            foreach (var name in names)
            {
                var v = Get(o, name);
                if (v == null || v.Type == JTokenType.Null) continue;
                return v.Type == JTokenType.String ? (string)v : v.ToString();
            }
            return "";
        }

        private static DateTime? ParseDate(string s)
        {
            DateTime dt;
            return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                                     System.Globalization.DateTimeStyles.RoundtripKind, out dt)
                ? (DateTime?)dt
                : null;
        }

        private static bool IsHttpUrl(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u)
                   && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>只对 GitHub 系的链接套加速前缀，别把用户自建的清单地址也绕到第三方节点</summary>
        private static bool IsGitHubUrl(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            var h = u.Host;
            return h.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                   || h.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
                   || h.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        private static string HostOf(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) ? u.Host : "自定义地址";
        }

        private static string FileNameFromUrl(string url)
        {
            try
            {
                var name = Uri.UnescapeDataString(new Uri(url).AbsolutePath);
                name = Path.GetFileName(name);
                return string.IsNullOrWhiteSpace(name) ? "" : name;
            }
            catch { return ""; }
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(cleaned) || cleaned == "." || cleaned == "..")
                cleaned = "TaskScheduler-setup.exe";
            // 只在完全没有扩展名时补 .exe：清单里若给的是 .msi / .zip，原样保留文件名，
            // 否则会出现 "setup.msi.exe" 这种既打不开又误导人的名字。
            if (!Path.HasExtension(cleaned)) cleaned += ".exe";
            return cleaned;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
