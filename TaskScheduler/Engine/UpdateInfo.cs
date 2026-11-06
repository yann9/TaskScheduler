using System;

namespace TaskScheduler.Engine
{
    /// <summary>
    /// 一条可用更新。既可以由 GitHub Releases 的 latest 接口转换而来，
    /// 也可以来自用户自定义的版本清单 JSON（字段见 UpdateService 的说明）。
    /// 只承载描述信息，不承载任何可执行类型，序列化时不涉及多态。
    /// </summary>
    public class UpdateInfo
    {
        /// <summary>版本号（如 1.2.0，允许带 v 前缀）</summary>
        public string Version { get; set; }

        /// <summary>发布标题</summary>
        public string Title { get; set; }

        /// <summary>发布说明正文</summary>
        public string Notes { get; set; }

        /// <summary>安装包的直接下载地址；为空表示只能打开下载页</summary>
        public string DownloadUrl { get; set; }

        /// <summary>
        /// 备用下载地址（清单里的 fallbackUrl）。主地址失败时自动换这条，
        /// 典型用法是「国内 Gitee 直链为主、GitHub Release 直链为备」；
        /// 备用地址若属于 GitHub 域名，客户端会自动套用加速节点。
        /// </summary>
        public string FallbackDownloadUrl { get; set; }

        /// <summary>发布页地址（下载失败 / 无安装包时给用户兜底）</summary>
        public string PageUrl { get; set; }

        /// <summary>安装包保存时用的文件名</summary>
        public string FileName { get; set; }

        public DateTime? PublishedAt { get; set; }

        /// <summary>
        /// 本次检查成功命中的加速节点前缀（空串 = 直连成功）。
        /// 只存在于内存中、不落盘：下载安装包时优先复用这条已经走通的线路，
        /// 也用于决定「打开下载页」要不要套同样的加速前缀。
        /// </summary>
        public string SourcePrefix { get; set; }

        /// <summary>是否具备「一键下载安装」的条件</summary>
        public bool CanInstall =>
            !string.IsNullOrWhiteSpace(DownloadUrl) && !string.IsNullOrWhiteSpace(FileName);
    }

    /// <summary>一次检查更新的结果。失败也要有明确原因，不能只弹一句"检查失败"。</summary>
    public class UpdateCheckResult
    {
        public bool Success { get; set; }

        /// <summary>失败原因（含网络错误、清单格式错误等）</summary>
        public string Error { get; set; }

        /// <summary>是否存在比当前版本更新的发布</summary>
        public bool HasUpdate { get; set; }

        public UpdateInfo Info { get; set; }

        /// <summary>本次检查的来源描述（GitHub Releases / 自定义清单地址），展示给用户便于排查</summary>
        public string Source { get; set; }

        public static UpdateCheckResult Failed(string error, string source = null)
            => new UpdateCheckResult { Success = false, Error = error ?? "未知错误", Source = source };

        public static UpdateCheckResult UpToDate(string source)
            => new UpdateCheckResult { Success = true, HasUpdate = false, Source = source };

        public static UpdateCheckResult Newer(UpdateInfo info, string source)
            => new UpdateCheckResult { Success = true, HasUpdate = true, Info = info, Source = source };
    }
}
