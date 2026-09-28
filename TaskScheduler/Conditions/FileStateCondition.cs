using System;
using System.IO;

namespace TaskScheduler.Conditions
{
    /// <summary>
    /// 文件 / 目录状态条件：路径是否存在、是否刚被改过、是否已经稳定下来、大小是否够。
    ///
    /// 最常用的两个组合：
    /// - 「某文件存在」→ 等上游导出完成后才跑后续处理；
    /// - 「最后修改超过 N 分钟」→ 文件已经写完、不再被占用，这时候搬运 / 上传才不会读到半个文件。
    /// </summary>
    public class FileStateCondition : ConditionBase
    {
        public override ConditionType Type => ConditionType.FilePath;

        /// <summary>文件或目录路径</summary>
        private string _path = "";
        public string Path
        {
            get => _path;
            set => SetField(ref _path, value);
        }

        private FileStateMode _mode = FileStateMode.Exists;
        public FileStateMode Mode
        {
            get => _mode;
            set => SetField(ref _mode, value);
        }

        /// <summary>阈值：按 Mode 解释为分钟数或 MB 数</summary>
        private int _value = 10;
        public int Value
        {
            get => _value;
            set => SetField(ref _value, value);
        }

        protected override bool EvaluateCore()
        {
            var p = (Path ?? "").Trim();
            if (p.Length == 0) return false;

            bool isDir = Directory.Exists(p);
            if (!isDir && !File.Exists(p))
                return Mode == FileStateMode.NotExists;   // 路径不存在时只有"不存在"成立

            switch (Mode)
            {
                case FileStateMode.Exists:
                    return true;

                case FileStateMode.NotExists:
                    return false;

                case FileStateMode.ModifiedWithinMinutes:
                    return AgeMinutes(p, isDir) <= Value;

                case FileStateMode.OlderThanMinutes:
                    return AgeMinutes(p, isDir) >= Value;

                case FileStateMode.SizeAtLeastMB:
                    if (isDir) return false;              // 目录不参与大小判定
                    return new FileInfo(p).Length >= (long)Value * 1024 * 1024;

                default:
                    return false;
            }
        }

        protected override string DescribeCore
        {
            get
            {
                var p = string.IsNullOrWhiteSpace(Path) ? "(未设置路径)" : Path.Trim();
                switch (Mode)
                {
                    case FileStateMode.Exists: return "存在：" + p;
                    case FileStateMode.NotExists: return "不存在：" + p;
                    case FileStateMode.ModifiedWithinMinutes: return $"{p} 在 {Value} 分钟内被修改过";
                    case FileStateMode.OlderThanMinutes: return $"{p} 已超过 {Value} 分钟未修改";
                    case FileStateMode.SizeAtLeastMB: return $"{p} 不小于 {Value} MB";
                    default: return p;
                }
            }
        }

        private static double AgeMinutes(string path, bool isDir)
        {
            var t = isDir ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
            return (DateTime.Now - t).TotalMinutes;
        }
    }
}
