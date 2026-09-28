using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TaskScheduler.Engine
{
    /// <summary>数据源里的单日安排（对应 holiday-cn 的 days 数组元素）</summary>
    public class HolidayDayDto
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("date")] public string Date { get; set; }
        [JsonProperty("isOffDay")] public bool IsOffDay { get; set; }
    }

    /// <summary>数据源里的单年文件（对应 YYYY.json）</summary>
    public class HolidayYearDto
    {
        [JsonProperty("year")] public int Year { get; set; }
        [JsonProperty("papers")] public List<string> Papers { get; set; }
        [JsonProperty("days")] public List<HolidayDayDto> Days { get; set; }
    }

    /// <summary>本地同步元信息（存在缓存目录的 _meta.json）</summary>
    public class HolidayMeta
    {
        [JsonProperty("lastSuccessUtc")] public DateTime? LastSuccessUtc { get; set; }
        [JsonProperty("lastAttemptUtc")] public DateTime? LastAttemptUtc { get; set; }
        [JsonProperty("lastError")] public string LastError { get; set; }
        [JsonProperty("mirrorIndex")] public int MirrorIndex { get; set; }
    }

    /// <summary>同步状态快照，供界面展示</summary>
    public sealed class HolidaySyncStatus
    {
        public bool IsRefreshing { get; set; }
        public DateTime? LastSuccessLocal { get; set; }
        public string Message { get; set; } = "尚未同步";
        public List<int> SyncedYears { get; set; } = new List<int>();
        /// <summary>数据源确认「该年度安排尚未公布」的年份</summary>
        public List<int> PendingYears { get; set; } = new List<int>();
    }

    /// <summary>
    /// 中国法定节假日在线同步。
    ///
    /// 数据源：GitHub 上的 NateScarlet/holiday-cn 项目 —— 纯静态 JSON，
    /// 每年国务院通知发布后几天内更新，并且**同时收录放假日与调休补班日**
    /// （days[].isOffDay 为 false 的就是补班日）。这一点是选它的关键：
    /// 大量免费节假日 API 只给「哪天放假」，不含调休 —— 那样春节前那个要上班的周六会被误跳。
    ///
    /// 设计立场：**在线数据只当数据源，本地缓存才是运行依据。**
    /// 任务判定发生在触发那一刻，绝不能因为 CDN 抖一下就让任务瞎跑或直接失败。
    /// 所以是三层：在线拉取 → 本地缓存 → 内置硬编码表（ChineseCalendar 里那份）。
    ///
    /// 多镜像回退、记住上次成功的镜像；自动刷新默认 7 天一次；
    /// 网络不通时失败后 1 小时内不重复尝试，避免反复卡启动。
    /// </summary>
    public static class HolidayProvider
    {
        /// <summary>缓存目录：%ProgramData%\TaskScheduler\holidays</summary>
        public static string CacheDir => Path.Combine(Paths.DataDir, "holidays");

        private static string MetaPath => Path.Combine(CacheDir, "_meta.json");

        /// <summary>自动刷新周期（天）</summary>
        public const int AutoRefreshDays = 7;

        /// <summary>失败后的静默期（分钟）——避免网络不通时每次启动都干等</summary>
        private const int FailBackoffMinutes = 60;

        private const int HttpTimeoutSeconds = 6;

        /// <summary>
        /// 镜像按实测稳定性排序：jsDelivr 最快（实测 0.5s），
        /// raw.githubusercontent 国内最不稳放最后。全部走 CDN，不依赖任何 API key。
        /// </summary>
        private static readonly string[] Mirrors =
        {
            "https://fastly.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{0}.json",
            "https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{0}.json",
            "https://ghproxy.net/https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{0}.json",
            "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{0}.json",
        };

        private static readonly HttpClient Http = CreateClient();
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static readonly object StatusLock = new object();

        private static HolidaySyncStatus _status = new HolidaySyncStatus();

        /// <summary>状态变化通知（可能在后台线程触发，界面订阅方自行切回 UI 线程）</summary>
        public static event EventHandler StatusChanged;

        public static HolidaySyncStatus Status
        {
            get { lock (StatusLock) { return _status; } }
        }

        private static HttpClient CreateClient()
        {
            // 不走系统代理：服务模式（Session 0）与内网环境下系统代理设置常常拿不到，
            // 直连反而更可靠。这一点和 HTTP 健康检查条件里的处理保持一致。
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds) };
            try { c.DefaultRequestHeaders.UserAgent.ParseAdd("TaskScheduler/1.0"); } catch { }
            return c;
        }

        // ------------------------------------------------------------ 对外入口

        /// <summary>
        /// 启动时调用：先同步读本地缓存（纯本地 IO，毫秒级），再后台异步刷新。
        /// 不阻塞启动，异常一律吞掉 —— 网络问题绝不该影响程序可用性。
        /// </summary>
        public static void Initialize()
        {
            try { LoadFromCache(); }
            catch (Exception ex) { Log.Write("加载节假日缓存失败：" + ex.Message, "WARN"); }

            Task.Run(async () =>
            {
                try { await RefreshAsync(false).ConfigureAwait(false); }
                catch (Exception ex) { Log.Write("节假日同步异常：" + ex.Message, "WARN"); }
            });
        }

        /// <summary>
        /// 只读本地缓存并生效。返回是否加载到了任何年份。
        /// </summary>
        public static bool LoadFromCache()
        {
            var years = new List<CalendarYear>();
            try
            {
                if (Directory.Exists(CacheDir))
                {
                    foreach (var f in Directory.GetFiles(CacheDir, "*.json"))
                    {
                        var name = Path.GetFileName(f);
                        if (name.StartsWith("_", StringComparison.Ordinal)) continue;   // 跳过 _meta.json

                        var parsed = ParseYearJson(File.ReadAllText(f), GuessYearFromName(name));
                        if (parsed != null && parsed.Holidays.Count + parsed.MakeupWorkdays.Count > 0)
                        {
                            parsed.Source = "已同步";
                            years.Add(parsed);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("读取节假日缓存目录失败：" + ex.Message, "WARN");
            }

            ChineseCalendar.ApplyExternal(years);

            var meta = LoadMeta();
            UpdateStatus(s =>
            {
                s.SyncedYears = years.Select(y => y.Year).OrderBy(y => y).ToList();
                s.LastSuccessLocal = meta.LastSuccessUtc?.ToLocalTime();
                s.Message = BuildMessage(years.Count, s.PendingYears, meta.LastError);
            });
            return years.Count > 0;
        }

        /// <summary>
        /// 在线刷新。<paramref name="force"/> = false 时受自动刷新周期与失败静默期约束；
        /// true 为手动刷新，无视节流。同一时刻只允许一个刷新任务在跑。
        /// </summary>
        public static async Task RefreshAsync(bool force)
        {
            if (!await Gate.WaitAsync(0).ConfigureAwait(false)) return;   // 已有刷新在跑，直接返回
            try
            {
                var meta = LoadMeta();

                if (!force)
                {
                    var now = DateTime.UtcNow;
                    if (meta.LastSuccessUtc.HasValue &&
                        (now - meta.LastSuccessUtc.Value).TotalDays < AutoRefreshDays)
                    {
                        Log.Write($"节假日数据 {AutoRefreshDays} 天内已同步过，跳过（上次 {meta.LastSuccessUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}）");
                        return;
                    }
                    if (meta.LastAttemptUtc.HasValue && meta.LastError != null &&
                        (now - meta.LastAttemptUtc.Value).TotalMinutes < FailBackoffMinutes)
                    {
                        Log.Write($"距上次同步失败不足 {FailBackoffMinutes} 分钟，暂不重试");
                        return;
                    }
                }

                UpdateStatus(s => { s.IsRefreshing = true; s.Message = "正在同步节假日数据…"; });
                Log.Write("开始同步节假日数据…");

                int baseYear = DateTime.Now.Year;
                var wanted = new[] { baseYear, baseYear + 1, baseYear + 2 };

                var fetched = new List<FetchResult>();
                var pending = new List<int>();
                var errors = new List<string>();
                int mirrorIdx = Math.Max(0, Math.Min(meta.MirrorIndex, Mirrors.Length - 1));

                foreach (var year in wanted)
                {
                    var r = await FetchYearAsync(year, mirrorIdx).ConfigureAwait(false);
                    if (r.MirrorUsed >= 0) mirrorIdx = r.MirrorUsed;   // 成功/应答的镜像下次优先用

                    if (r.Year != null)
                    {
                        r.Year.Source = "已同步";
                        fetched.Add(r);
                        Log.Write($"节假日 {year} 年：已获取 {r.Year.Holidays.Count} 个放假日、{r.Year.MakeupWorkdays.Count} 个补班日");
                    }
                    else if (r.IsPending)
                    {
                        pending.Add(year);
                        Log.Write($"节假日 {year} 年：数据源尚无安排（国务院通知未发布）");
                    }
                    else
                    {
                        errors.Add($"{year}：{r.Error}");
                        Log.Write($"节假日 {year} 年获取失败：{r.Error}", "WARN");
                    }
                }

                // 有任何一次成功应答（哪怕数据是空的）就算通信正常，刷新周期重新计时
                bool reachable = fetched.Count > 0 || pending.Count > 0;

                foreach (var r in fetched) SaveCache(r.Year, r.RawJson);

                if (reachable)
                {
                    meta.LastSuccessUtc = DateTime.UtcNow;
                    meta.MirrorIndex = mirrorIdx;
                    meta.LastError = errors.Count == 0 ? null : string.Join("；", errors);
                }
                else
                {
                    meta.LastError = errors.Count > 0 ? string.Join("；", errors) : "网络不可达";
                }
                meta.LastAttemptUtc = DateTime.UtcNow;
                SaveMeta(meta);

                // 重新全量加载：本次拉到的年份会覆盖旧缓存，没拉到的年份沿用已有缓存文件
                LoadFromCache();
                UpdateStatus(s =>
                {
                    s.PendingYears = pending.OrderBy(y => y).ToList();
                    s.Message = BuildMessage(s.SyncedYears.Count, s.PendingYears, meta.LastError);
                });
                Log.Write("节假日同步完成：" + Status.Message);
            }
            finally
            {
                UpdateStatus(s => s.IsRefreshing = false);
                Gate.Release();
            }
        }

        // ------------------------------------------------------------ 拉取

        private sealed class FetchResult
        {
            public CalendarYear Year;
            /// <summary>数据源原始响应，原样落盘（保留节假日名称与国务院原文链接，便于人工核对）</summary>
            public string RawJson;
            public string Error;
            public bool IsPending;
            public int MirrorUsed = -1;
        }

        /// <summary>逐年拉取，依次试各镜像。startMirror 为上次成功的镜像序号（做起点轮转）。</summary>
        private static async Task<FetchResult> FetchYearAsync(int year, int startMirror)
        {
            var last = new FetchResult();

            for (int i = 0; i < Mirrors.Length; i++)
            {
                int idx = (startMirror + i) % Mirrors.Length;
                var url = string.Format(Mirrors[idx], year);

                try
                {
                    var json = await Http.GetStringAsync(url).ConfigureAwait(false);

                    // CDN 出错时会返回 HTML 错误页（状态码仍是 200），必须识别出来：
                    // 否则会被当成"该年度尚未公布"，把刷新周期重置，接下来一周都不再重试。
                    var head = json == null ? "" : json.TrimStart();
                    if (head.Length == 0 || head[0] != '{')
                    {
                        last.Error = "响应不是 JSON（疑似 CDN 错误页）";
                        continue;
                    }

                    var parsed = ParseYearJson(json, year);

                    if (parsed == null)
                    {
                        // 能拿到响应但解析不出内容 —— 视为「该年度尚无安排」而不是网络故障。
                        // 数据源对未公布年份返回的是 days:[]，属于正常应答。
                        last.IsPending = true;
                        last.Error = "数据为空";
                        last.MirrorUsed = idx;
                        return last;
                    }

                    last.Year = parsed;
                    last.RawJson = json;
                    last.MirrorUsed = idx;
                    return last;
                }
                catch (Exception ex)
                {
                    last.Error = Shorten(ex.Message);
                }
            }

            return last;
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "未知错误";
            return s.Length <= 80 ? s : s.Substring(0, 80) + "…";
        }

        // ------------------------------------------------------------ 解析与缓存

        /// <summary>解析单年 JSON；没有有效数据返回 null</summary>
        private static CalendarYear ParseYearJson(string json, int fallbackYear)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            HolidayYearDto dto;
            try { dto = JsonConvert.DeserializeObject<HolidayYearDto>(json); }
            catch { return null; }

            if (dto == null || dto.Days == null || dto.Days.Count == 0) return null;

            var year = new CalendarYear
            {
                Year = dto.Year > 0 ? dto.Year : fallbackYear,
                Papers = dto.Papers ?? new List<string>()
            };

            foreach (var d in dto.Days)
            {
                if (d == null || string.IsNullOrWhiteSpace(d.Date)) continue;
                if (!DateTime.TryParseExact(d.Date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                            DateTimeStyles.None, out var dt))
                    continue;

                // isOffDay = true 是放假日；false 是调休补班日（周末但要上班）
                if (d.IsOffDay) year.Holidays.Add(dt.Date);
                else year.MakeupWorkdays.Add(dt.Date);
            }

            return year.Holidays.Count + year.MakeupWorkdays.Count == 0 ? null : year;
        }

        private static int GuessYearFromName(string fileName)
        {
            var n = Path.GetFileNameWithoutExtension(fileName);
            return int.TryParse(n, out var y) ? y : 0;
        }

        /// <summary>
        /// 原样保存数据源响应。
        /// 不重新序列化 —— 保住 days[].name（"元旦"/"春节"）和 papers（国务院原文链接），
        /// 便于人工核对缓存内容；以后要用到新字段时也不用重新联网拉。
        /// </summary>
        private static void SaveCache(CalendarYear y, string rawJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rawJson)) return;
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(Path.Combine(CacheDir, y.Year + ".json"), rawJson);
            }
            catch (Exception ex)
            {
                Log.Write($"写入节假日缓存失败（{y.Year}）：{ex.Message}", "WARN");
            }
        }

        private static HolidayMeta LoadMeta()
        {
            try
            {
                if (File.Exists(MetaPath))
                {
                    var m = JsonConvert.DeserializeObject<HolidayMeta>(File.ReadAllText(MetaPath));
                    if (m != null) return m;
                }
            }
            catch { }
            return new HolidayMeta();
        }

        private static void SaveMeta(HolidayMeta meta)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(MetaPath, JsonConvert.SerializeObject(meta, Formatting.Indented));
            }
            catch { }
        }

        // ------------------------------------------------------------ 状态

        private static void UpdateStatus(Action<HolidaySyncStatus> mutate)
        {
            HolidaySyncStatus snapshot;
            lock (StatusLock)
            {
                mutate(_status);
                snapshot = _status;
            }
            try { StatusChanged?.Invoke(null, EventArgs.Empty); } catch { }
        }

        private static string BuildMessage(int syncedCount, List<int> pending, string lastError)
        {
            if (syncedCount == 0)
                return lastError == null
                    ? "尚未同步到节假日数据，正在使用内置表（仅 2025–2026）"
                    : $"同步失败（{lastError}），正在使用内置表（仅 2025–2026）";

            var meta = LoadMeta();
            var when = meta.LastSuccessUtc?.ToLocalTime().ToString("MM-dd HH:mm") ?? "未知时间";
            var msg = $"已同步 {syncedCount} 个年度（更新于 {when}）";
            if (pending != null && pending.Count > 0)
                msg += $"；{string.Join("、", pending)} 年安排尚未公布";
            return msg;
        }
    }
}
