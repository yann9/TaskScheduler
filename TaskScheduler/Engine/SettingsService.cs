using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace TaskScheduler.Engine
{
    /// <summary>
    /// 设置的加载 / 保存 / 变更广播。
    ///
    /// 三条硬约束：
    /// 1) 读失败必须回退到默认值。设置文件坏了不能让整个程序起不来；这里连日志都不写 ——
    ///    Log 自己会读设置（滚动阈值），在加载路径里写日志就是递归。
    /// 2) 写盘走"临时文件 + Replace"，避免写到一半断电留下半截 JSON 把设置清空。
    /// 3) 反序列化关掉 TypeNameHandling。这个文件在 ProgramData 下、本机用户可写，
    ///    设置里没有多态，没有任何理由接受 $type（那是提权入口）。
    /// </summary>
    public static class SettingsService
    {
        private static readonly object _lock = new object();
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            // 存成字符串（"MinimizeToTray"）而不是数字：用户手改这个文件时能看懂，
            // 也不会因为将来在枚举中间插一个成员就让老文件语义漂移。
            Converters = { new StringEnumConverter() }
        };

        private static AppSettings _current;
        private static string _path;

        /// <summary>设置被保存（或重新加载）后触发，供界面刷新依赖设置的显示</summary>
        public static event EventHandler Changed;

        public static string FilePath => string.IsNullOrEmpty(_path) ? Paths.SettingsFilePath : _path;

        /// <summary>当前设置（懒加载，永远非 null）</summary>
        public static AppSettings Current
        {
            get
            {
                lock (_lock)
                {
                    if (_current == null)
                    {
                        _current = LoadFrom(FilePath);
                        Hook(_current);
                    }
                    return _current;
                }
            }
        }

        /// <summary>指定设置文件位置（测试用；不调用则用 ProgramData 下的默认路径）</summary>
        public static void Init(string path = null)
        {
            lock (_lock)
            {
                _path = path ?? Paths.SettingsFilePath;
                _current = LoadFrom(_path);
                Hook(_current);
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>重新从磁盘加载（服务端在界面改完设置后想立刻生效时用）</summary>
        public static AppSettings Reload()
        {
            lock (_lock)
            {
                var loaded = LoadFrom(FilePath);
                if (_current == null) { _current = loaded; Hook(_current); }
                else _current.CopyFrom(loaded);
            }
            Changed?.Invoke(null, EventArgs.Empty);
            return Current;
        }

        /// <summary>把当前设置写回磁盘</summary>
        public static void Save()
        {
            AppSettings snapshot;
            lock (_lock)
            {
                snapshot = Current;
                snapshot.Normalize();
            }
            WriteTo(FilePath, snapshot);
        }

        private static void Hook(AppSettings s)
        {
            // 任何一个字段被改动，都广播一次 Changed —— 界面据此刷新（例如"保留最近 N 条"的说明文字）。
            // 落盘不在这里做：调用方（设置窗口）会做节流保存，免得拖动数字框时每敲一下写一次磁盘。
            s.PropertyChanged += (_, __) => Changed?.Invoke(null, EventArgs.Empty);
        }

        private static AppSettings LoadFrom(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var s = JsonConvert.DeserializeObject<AppSettings>(json, Json);
                        if (s != null) { s.Normalize(); return s; }
                    }
                }
            }
            catch
            {
                // 故意吞掉：设置坏了就用默认值启动，绝不让程序起不来。
                // 用户会在设置窗口看到"恢复默认值"的实际生效结果。
            }
            return new AppSettings();
        }

        private static void WriteTo(string path, AppSettings settings)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(settings, Json);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);

                if (!File.Exists(path))
                {
                    File.Move(tmp, path);
                    return;
                }

                try
                {
                    File.Replace(tmp, path, null, true);
                }
                catch
                {
                    // 个别文件系统不支持 Replace → 退化为直接覆盖
                    File.Copy(tmp, path, true);
                    try { File.Delete(tmp); } catch { }
                }
            }
            catch
            {
                // 写不进去（磁盘满 / 权限）也不该让界面崩：设置只是没持久化而已
            }
        }
    }
}
