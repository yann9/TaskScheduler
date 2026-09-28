using System;
using System.Collections.Generic;
using System.IO;

namespace TaskScheduler.Conditions
{
    /// <summary>
    /// 磁盘空间条件：指定分区的可用空间达到 / 低于阈值。
    /// 典型用法：「D 盘剩余不足 20GB 时才跑清理脚本」（RequireAtLeast = false）。
    ///
    /// 阈值单位是 MB，不是 GB —— 让「剩余不足 500MB 才告警」这种小阈值也能填。
    /// 注意：映射的网络驱动器是"会话级"的，服务模式（Session 0）下看不到；
    /// 只在服务模式下会被判为不满足。
    /// </summary>
    public class DiskSpaceCondition : ConditionBase
    {
        public override ConditionType Type => ConditionType.DiskSpace;

        /// <summary>盘符，例如 "C:"；留空表示系统盘</summary>
        private string _drive = "";
        public string Drive
        {
            get => _drive;
            set => SetField(ref _drive, value);
        }

        /// <summary>阈值，单位 MB</summary>
        private int _thresholdMB = 20480;
        public int ThresholdMB
        {
            get => _thresholdMB;
            set => SetField(ref _thresholdMB, value);
        }

        /// <summary>true = 剩余不少于阈值；false = 剩余少于阈值</summary>
        private bool _requireAtLeast = true;
        public bool RequireAtLeast
        {
            get => _requireAtLeast;
            set => SetField(ref _requireAtLeast, value);
        }

        protected override bool EvaluateCore()
        {
            var free = GetFreeSpaceMB(Drive);
            if (free < 0) return false;                       // 盘不存在 / 未就绪 → 视为不满足
            return RequireAtLeast ? free >= ThresholdMB : free < ThresholdMB;
        }

        protected override string DescribeCore
        {
            get
            {
                var d = NormalizeDrive(Drive);
                var t = FormatSize(ThresholdMB);
                return RequireAtLeast ? $"{d} 剩余空间不少于 {t}" : $"{d} 剩余空间不足 {t}";
            }
        }

        // ---------- 工具 ----------

        /// <summary>把 "C" / "C:" / "c:\" 统一成 "C:\"；留空则取系统盘</summary>
        public static string NormalizeDrive(string drive)
        {
            var name = (drive ?? "").Trim();
            if (name.Length == 0)
            {
                try { return Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"; }
                catch { return "C:\\"; }
            }
            if (name.Length == 1) name += ":";
            if (!name.EndsWith("\\")) name += "\\";
            return name;
        }

        /// <summary>可用空间（MB）；盘不存在或未就绪返回 -1</summary>
        public static long GetFreeSpaceMB(string drive)
        {
            try
            {
                var di = new DriveInfo(NormalizeDrive(drive));
                if (!di.IsReady) return -1;
                return di.AvailableFreeSpace / 1024 / 1024;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>枚举本机固定盘与可移动盘，供界面下拉选择</summary>
        public static List<string> GetDrives()
        {
            var list = new List<string>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                        list.Add(d.Name.TrimEnd('\\'));      // "C:"
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        private static string FormatSize(int mb)
        {
            if (mb < 1024) return mb + " MB";
            return (mb / 1024.0).ToString("0.##") + " GB";
        }
    }
}
