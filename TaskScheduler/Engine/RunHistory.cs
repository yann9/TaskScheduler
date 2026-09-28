using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace TaskScheduler.Engine
{
    /// <summary>一次任务执行的结果分类</summary>
    public enum RunOutcome
    {
        /// <summary>动作执行完成（退出码 0 / HTTP 2xx / 文件操作成功）</summary>
        Success,

        /// <summary>动作执行失败（抛异常、退出码非 0、HTTP 状态码不符）</summary>
        Failed,

        /// <summary>没有执行：条件不满足、上一次还在跑、未配置动作</summary>
        Skipped,

        /// <summary>执行中被"停止引擎"取消</summary>
        Canceled
    }

    /// <summary>运行记录的查询条件（界面直接构造，服务模式下作为 IPC 载荷传输）</summary>
    public class RunHistoryQuery
    {
        public string TaskId { get; set; }
        public RunOutcome? Outcome { get; set; }
        public string Keyword { get; set; }
        public int Limit { get; set; }
    }

    /// <summary>
    /// 一条运行记录。
    /// 只保留"事后排障真正要看"的字段 —— 时间、来源、结果、耗时、原因、动作输出摘要。
    /// 动作输出会截断（见 <see cref="MaxOutputChars"/>）：一个刷屏的脚本能产出几十 MB stdout，
    /// 原样存进 runs.json 会让这个文件失控。
    /// </summary>
    public class RunRecord
    {
        /// <summary>动作输出保留的最大字符数</summary>
        public const int MaxOutputChars = 4000;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string TaskId { get; set; }
        public string TaskName { get; set; }

        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double DurationMs { get; set; }

        /// <summary>触发来源：定时触发 / 手动执行 / 错过补偿 / 系统事件…</summary>
        public string Trigger { get; set; } = "";

        public RunOutcome Outcome { get; set; }

        /// <summary>结果的一句话说明（"成功" / "失败：exit code 1" / "跳过：条件不满足(未接通电源)"）</summary>
        public string Message { get; set; } = "";

        /// <summary>动作的标准输出 / 错误输出摘要，可能为空</summary>
        public string Output { get; set; } = "";

        [JsonIgnore] public string StartTimeText => StartTime.ToString("yyyy-MM-dd HH:mm:ss");

        [JsonIgnore]
        public string DurationText => DurationMs < 1000
            ? $"{DurationMs:F0} ms"
            : DurationMs < 60000
                ? $"{DurationMs / 1000:F1} s"
                : $"{DurationMs / 60000:F1} min";

        [JsonIgnore]
        public string OutcomeText => Outcome switch
        {
            RunOutcome.Success => "成功",
            RunOutcome.Failed => "失败",
            RunOutcome.Skipped => "已跳过",
            RunOutcome.Canceled => "已取消",
            _ => Outcome.ToString()
        };

        [JsonIgnore] public bool IsFailure => Outcome == RunOutcome.Failed || Outcome == RunOutcome.Canceled;

        /// <summary>列表里显示的"时间 + 来源"摘要</summary>
        [JsonIgnore]
        public string Summary => string.IsNullOrEmpty(Trigger)
            ? StartTimeText
            : StartTimeText + "　" + Trigger;
    }

    /// <summary>
    /// 运行记录仓库（%ProgramData%\TaskScheduler\runs.json）。
    ///
    /// 环形保留：只留最近的 N 条（N 来自设置，默认 200）。任务每隔几分钟跑一次、
    /// 常年无人清理的话，不裁剪的记录文件会一直涨到几百 MB。
    ///
    /// 与 tasks.json 一样，写盘原子、损坏文件改名隔离 —— 但读失败**不必**惊动用户：
    /// 运行记录丢了不影响任务执行，重建即可。
    /// </summary>
    public class RunHistoryStore
    {
        private readonly object _lock = new object();
        private readonly List<RunRecord> _records = new List<RunRecord>();
        private readonly string _path;

        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            // 运行记录是纯数据（没有多态），关掉 $type：这个文件本地用户可写，
            // 而反序列化方可能是 LocalSystem，没有任何理由接受类型指示。
            TypeNameHandling = TypeNameHandling.None,
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            Converters = { new StringEnumConverter() }
        };

        public RunHistoryStore(string path = null)
        {
            _path = string.IsNullOrEmpty(path) ? Paths.RunHistoryFilePath : path;
            Load();
        }

        public int Count { get { lock (_lock) return _records.Count; } }

        /// <summary>按时间倒序（最新在前）的只读快照</summary>
        public IReadOnlyList<RunRecord> All
        {
            get { lock (_lock) return _records.ToList(); }
        }

        /// <summary>追加一条记录并按设置裁剪，随后落盘</summary>
        public void Add(RunRecord record)
        {
            if (record == null) return;
            if (record.Output != null && record.Output.Length > RunRecord.MaxOutputChars)
                record.Output = record.Output.Substring(0, RunRecord.MaxOutputChars) + "\n…（输出过长已截断）";

            lock (_lock)
            {
                // 最新的放最前面：界面默认展示"最近发生了什么"，不必再排序
                _records.Insert(0, record);
                TrimLocked();
            }
            Save();
        }

        /// <summary>按条件查询（全部参数可空；返回倒序快照）</summary>
        public IReadOnlyList<RunRecord> Query(string taskId = null, RunOutcome? outcome = null,
                                              string keyword = null, int limit = 0)
        {
            lock (_lock)
            {
                IEnumerable<RunRecord> q = _records;

                if (!string.IsNullOrEmpty(taskId))
                    q = q.Where(r => r.TaskId == taskId);

                if (outcome.HasValue)
                    q = q.Where(r => r.Outcome == outcome.Value);

                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    var k = keyword.Trim();
                    q = q.Where(r =>
                        (r.TaskName ?? "").IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (r.Message ?? "").IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (r.Trigger ?? "").IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (limit > 0) q = q.Take(limit);
                return q.ToList();
            }
        }

        public void Clear()
        {
            lock (_lock) _records.Clear();
            Save();
        }

        public void ClearTask(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return;
            lock (_lock) _records.RemoveAll(r => r.TaskId == taskId);
            Save();
        }

        private void TrimLocked()
        {
            var max = SettingsService.Current.MaxRunRecords;
            if (max < 10) max = 10;
            if (_records.Count > max) _records.RemoveRange(max, _records.Count - max);
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var json = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(json)) return;

                var list = JsonConvert.DeserializeObject<List<RunRecord>>(json, Json);
                if (list == null) return;

                lock (_lock)
                {
                    _records.Clear();
                    // 文件里本来就是倒序存的，重新排一遍是为了兼容被手工编辑过的文件
                    _records.AddRange(list.OrderByDescending(r => r.StartTime));
                    TrimLocked();
                }
            }
            catch
            {
                // 读不出来就当没有历史，并把坏文件挪走（下次写入会重建），
                // 这样也不会每启动一次就在同一个坏文件上失败一次
                try
                {
                    if (File.Exists(_path))
                        File.Move(_path, _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                }
                catch { }
            }
        }

        private void Save()
        {
            List<RunRecord> snapshot;
            lock (_lock) snapshot = _records.ToList();

            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(snapshot, Json);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);

                if (!File.Exists(_path))
                {
                    File.Move(tmp, _path);
                    return;
                }

                try
                {
                    File.Replace(tmp, _path, null, true);
                }
                catch
                {
                    File.Copy(tmp, _path, true);
                    try { File.Delete(tmp); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Error("保存运行记录失败：" + ex.Message);
            }
        }
    }
}
