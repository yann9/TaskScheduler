using System;
using System.IO;

namespace TaskScheduler.Triggers
{
    /// <summary>
    /// 文件 / 文件夹监听触发器：目录内出现新建 / 变更 / 重命名 / 删除时触发。
    /// </summary>
    public class FileWatcherTrigger : ITrigger
    {
        public TriggerType Type => TriggerType.FileWatcher;

        public string Path { get; set; } = "";
        public string Filter { get; set; } = "*.*";
        public bool IncludeSubdirectories { get; set; }
        public FileWatcherChangeType ChangeType { get; set; } = FileWatcherChangeType.Created;
        public bool FireOnStartupIfExists { get; set; }

        /// <summary>
        /// 防抖窗口（毫秒）：事件进来后不立刻触发，等这段时间内没有新事件了再触发一次。
        ///
        /// 为什么必须要：编辑器保存文件基本都是"写临时文件 → 改名到目标"，
        /// 配合「全部变化」会连着触发两三次；下游是"上传 / 转码 / 备份"这类动作时，
        /// 同一次保存被当成多次变更，任务就会白跑好几遍，甚至读到只写了一半的文件。
        ///
        /// 用末尾触发（等静默）而不是立刻触发：文件还在写的时候触发，下游读到的是半个文件。
        /// 0 = 关闭防抖，每次事件都触发。
        /// </summary>
        public int DebounceMilliseconds { get; set; } = 500;

        public DateTime? NextRunTime => null;

        public event EventHandler<TriggerFiredEventArgs> Fired;

        private FileSystemWatcher _watcher;
        private bool _started;
        private System.Threading.Timer _debounceTimer;
        private readonly object _debounceLock = new object();
        private string _pendingMessage;

        public void Start()
        {
            if (_started) return;
            if (string.IsNullOrWhiteSpace(Path) || !Directory.Exists(Path)) return;
            _started = true;

            _watcher = new FileSystemWatcher(Path, Filter)
            {
                IncludeSubdirectories = IncludeSubdirectories,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName
            };

            switch (ChangeType)
            {
                case FileWatcherChangeType.Created:
                    _watcher.Created += OnCreated; break;
                case FileWatcherChangeType.Changed:
                    _watcher.Changed += OnChanged; break;
                case FileWatcherChangeType.Renamed:
                    _watcher.Renamed += OnRenamed; break;
                case FileWatcherChangeType.Deleted:
                    _watcher.Deleted += OnDeleted; break;
                case FileWatcherChangeType.All:
                    _watcher.Created += OnCreated;
                    _watcher.Changed += OnChanged;
                    _watcher.Renamed += OnRenamed;
                    _watcher.Deleted += OnDeleted;
                    break;
            }

            _watcher.EnableRaisingEvents = true;

            if (FireOnStartupIfExists)
            {
                try
                {
                    var existing = Directory.GetFiles(Path, Filter);
                    // 启动时已存在的文件合并成一条，别在开机瞬间把下游任务连打 N 次
                    if (existing.Length > 0)
                        Post("启动时已存在文件", $"{existing.Length} 个");
                }
                catch { }
            }
        }

        public void Stop()
        {
            _started = false;
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
            lock (_debounceLock)
            {
                if (_debounceTimer != null) { _debounceTimer.Dispose(); _debounceTimer = null; }
                _pendingMessage = null;
            }
        }

        private void OnCreated(object s, FileSystemEventArgs e) => Post("新建文件", e.FullPath);
        private void OnChanged(object s, FileSystemEventArgs e) => Post("文件变更", e.FullPath);
        private void OnRenamed(object s, RenamedEventArgs e) => Post("文件重命名", e.FullPath);
        private void OnDeleted(object s, FileSystemEventArgs e) => Post("文件删除", e.FullPath);

        /// <summary>事件入口：按防抖窗口合并后，再交给 Flush 真正触发</summary>
        private void Post(string msg, string detail)
        {
            if (!_started) return;
            var text = detail == null ? msg : $"{msg}：{detail}";

            if (DebounceMilliseconds <= 0) { Fire(text); return; }

            var ms = Math.Max(50, DebounceMilliseconds);
            lock (_debounceLock)
            {
                _pendingMessage = text;   // 合并：静默期内只保留最后一条
                if (_debounceTimer == null)
                    _debounceTimer = new System.Threading.Timer(
                        _ => Flush(), null, ms, System.Threading.Timeout.Infinite);
                else
                    _debounceTimer.Change(ms, System.Threading.Timeout.Infinite);
            }
        }

        /// <summary>静默期结束，触发一次</summary>
        private void Flush()
        {
            string text;
            lock (_debounceLock)
            {
                text = _pendingMessage;
                _pendingMessage = null;
            }
            if (text != null) Fire(text);
        }

        private void Fire(string text)
        {
            if (!_started) return;
            Fired?.Invoke(this, new TriggerFiredEventArgs { Message = text });
        }

        public string Describe()
        {
            var dir = string.IsNullOrWhiteSpace(Path) ? "(未设置路径)" : Path;
            var chg = ChangeType switch
            {
                FileWatcherChangeType.Created => "新建文件",
                FileWatcherChangeType.Changed => "文件变更",
                FileWatcherChangeType.Renamed => "重命名",
                FileWatcherChangeType.Deleted => "删除",
                _ => "全部变化"
            };
            return $"监听目录 {dir}（{chg}）";
        }
    }
}
