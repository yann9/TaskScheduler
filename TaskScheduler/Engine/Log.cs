using System;
using System.IO;

namespace TaskScheduler.Engine
{
    /// <summary>
    /// 极简日志：同时写入文件（AppData）并广播给界面。
    /// </summary>
    public static class Log
    {
        public static event EventHandler<string> Appended;

        private static readonly object _lock = new object();
        private static string _file = "";
        private static readonly System.Collections.Generic.List<string> _buffer
            = new System.Collections.Generic.List<string>();
        private const int MaxBuffer = 1000;

        /// <summary>日志文件达到该大小就滚动一次（旧文件保留为 .1，便于事后排查）。阈值来自设置，用户可在设置窗口改。</summary>
        private static long MaxFileBytes
        {
            get
            {
                var mb = SettingsService.Current.MaxLogFileMB;
                if (mb < 1) mb = 1;
                return mb * 1024L * 1024L;
            }
        }

        /// <summary>每写这么多行检查一次文件大小（每次都查会带来无谓的磁盘访问）</summary>
        private const int SizeCheckInterval = 200;

        private static int _writesSinceSizeCheck;

        public static void Init(string filePath)
        {
            _file = filePath;
            try { Directory.CreateDirectory(Path.GetDirectoryName(_file) ?? "."); }
            catch { }
        }

        public static void Write(string message, string level = "INFO")
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
            lock (_lock)
            {
                try
                {
                    // 服务模式会常年运行，日志只涨不裁的话单文件能长到几百 MB。
                    if (!string.IsNullOrEmpty(_file) && ++_writesSinceSizeCheck >= SizeCheckInterval)
                    {
                        _writesSinceSizeCheck = 0;
                        RollIfTooLarge();
                    }

                    if (!string.IsNullOrEmpty(_file))
                        File.AppendAllText(_file, line + Environment.NewLine);
                }
                catch { }
                _buffer.Add(line);
                while (_buffer.Count > MaxBuffer) _buffer.RemoveAt(0);
            }
            Appended?.Invoke(null, line);
        }

        /// <summary>超过阈值就把当前日志改名为 .1，下一行写入会重新创建文件</summary>
        private static void RollIfTooLarge()
        {
            try
            {
                if (string.IsNullOrEmpty(_file) || !File.Exists(_file)) return;
                if (new FileInfo(_file).Length <= MaxFileBytes) return;

                var old = _file + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(_file, old);
            }
            catch { }
        }

        /// <summary>返回最近 count 行日志（用于 UI/服务轮询）</summary>
        public static string Tail(int count)
        {
            lock (_lock)
            {
                var take = _buffer.Count <= count
                    ? _buffer
                    : _buffer.GetRange(_buffer.Count - count, count);
                return string.Join(Environment.NewLine, take);
            }
        }

        /// <summary>日志文件路径（日志窗口里展示"日志在哪"用）</summary>
        public static string FilePath => _file;

        /// <summary>
        /// 从日志文件尾部读取最多 maxLines 行。
        /// 内存缓冲（_buffer）只留 1000 行，日志窗口要能翻更早的内容，所以得落到文件上读。
        /// 用队列滚动而不是 ReadAllLines：几 MB 的日志是十万行级，没必要全部读进内存。
        /// </summary>
        public static string ReadTailFromFile(int maxLines)
        {
            try
            {
                if (string.IsNullOrEmpty(_file) || !File.Exists(_file)) return "";
                if (maxLines < 1) maxLines = 1;

                var q = new System.Collections.Generic.Queue<string>(maxLines);
                foreach (var line in File.ReadLines(_file))
                {
                    if (q.Count == maxLines) q.Dequeue();
                    q.Enqueue(line);
                }
                return string.Join(Environment.NewLine, q);
            }
            catch (Exception ex)
            {
                return "（读取日志文件失败：" + ex.Message + "）";
            }
        }

        /// <summary>删除日志文件（日志窗口的"清空日志文件"用；失败返回原因，成功返回 null）</summary>
        public static string ClearFile()
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(_file) || !File.Exists(_file)) return null;
                    File.Delete(_file);
                    _buffer.Clear();
                    return null;
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            }
        }

        public static void Error(string message) => Write(message, "ERROR");
    }
}