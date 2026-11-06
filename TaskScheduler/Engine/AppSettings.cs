using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TaskScheduler.Engine
{
    /// <summary>点主窗口右上角「×」时的行为</summary>
    public enum CloseAction
    {
        /// <summary>最小化到系统托盘（默认：任务继续按计划执行，双击托盘图标可恢复）</summary>
        MinimizeToTray,

        /// <summary>直接退出程序（本地托管模式下引擎随之停止）</summary>
        ExitApp
    }

    /// <summary>
    /// 全局设置（%ProgramData%\TaskScheduler\settings.json）。
    ///
    /// 界面与服务共用同一份文件：界面改完立即落盘，服务下一次读它来决定运行记录保留条数、
    /// 日志滚动阈值。继承 ObservableObject 是为了让设置窗口直接双向绑定 ——
    /// 改一个开关就是改这里的字段本身，不必在 ViewModel 里再抄一遍同名属性
    /// （抄一遍就多一处"改了这边忘了那边"）。
    ///
    /// 注意：新增字段必须给默认值。旧存档里没有该字段时反序列化后保留的就是这个默认值，
    /// 从而不会因为升级让用户已有行为发生变化。
    /// </summary>
    public partial class AppSettings : ObservableObject
    {
        // ===== 启动 =====
        [ObservableProperty] private bool _autoStartWithWindows;

        /// <summary>
        /// 开机自启时直接最小化到托盘（不弹主窗口）。
        ///
        /// 注意：这一项**只对"随 Windows 登录自动启动"那一次生效**，不针对手动双击图标。
        /// 实现方式是把 --minimized 写进自启注册表项的命令行（见 AutoStartRegistration.Enable），
        /// 程序启动时只看命令行参数。
        ///
        /// 早期版本是"启动时无条件读这个字段"，结果是用户手动双击桌面图标也什么都看不到 ——
        /// 和"点了图标没反应"的故障表现一模一样，很容易被当成程序坏了。
        /// </summary>
        [ObservableProperty] private bool _autoStartMinimized;

        /// <summary>
        /// 启动界面时自动启动引擎（本地托管）。
        /// 只在后台服务**未安装**时生效：服务已安装时执行权一律归服务，
        /// 程序会自动停用本地引擎，两个引擎不会同时运行（2026-09-24 周工拍板）。
        /// </summary>
        [ObservableProperty] private bool _autoStartEngine = true;

        // ===== 关闭行为 =====
        [ObservableProperty] private CloseAction _closeAction = CloseAction.MinimizeToTray;
        [ObservableProperty] private bool _showTrayHintOnClose = true;

        // ===== 运行记录 =====
        [ObservableProperty] private int _maxRunRecords = 200;
        [ObservableProperty] private bool _recordSuccessfulRuns = true;

        // ===== 日志 =====
        [ObservableProperty] private int _logTailLines = 300;
        [ObservableProperty] private int _maxLogFileMB = 5;

        // ===== 通知 =====
        [ObservableProperty] private bool _notifyOnTaskFailure = true;

        // ===== 电源 =====
        /// <summary>
        /// 任务运行期间阻止系统休眠（2026-09-30 新增，默认开）。
        /// 从触发（含条件等待窗口）到动作执行完毕，系统保持工作状态；
        /// 只拦"空闲自动睡眠"，不影响用户手动关机 / 睡眠。
        /// 另有托盘菜单「阻止系统休眠」做无限期手动保持，与这里共用引用计数。
        /// </summary>
        [ObservableProperty] private bool _preventSleepDuringTasks = true;

        // ===== 界面 =====
        [ObservableProperty] private bool _confirmOnDelete = true;
        [ObservableProperty] private int _taskListFontSize = 12;

        // ===== 更新 =====
        /// <summary>
        /// 启动界面后自动检查一次更新（每天最多一次；只有发现新版本才弹窗，失败静默记日志）。
        /// 默认开启但不会打扰：没网 / 清单不可达时只是不提示。
        /// </summary>
        [ObservableProperty] private bool _autoCheckUpdate = true;

        /// <summary>
        /// 自定义版本清单地址。留空 = 走内置的多源检查（国内镜像 + GitHub）。
        ///
        /// 填了这一项就以它为准，通常用于私有发布源 / 内网 / 自建站点；
        /// 国内用户一般用不着填，内置的 Gitee 镜像清单已经能直连。
        /// 格式见 UpdateService 注释与 README。
        /// </summary>
        [ObservableProperty] private string _updateManifestUrl = "";

        /// <summary>
        /// 国内直连的版本清单地址，默认指向 Gitee 制品仓库里本项目专属的固定文件：
        /// https://gitee.com/yann9/dist/raw/master/TaskScheduler/update.json
        ///
        /// 【为什么不是 Gitee 的 releases/latest】
        /// yann9/dist 是**所有项目共用**的 dist 制品库，按项目分子目录存放。
        /// 仓库级 latest 会指向别的项目最新发的那个 Release，tag 也会跨项目撞名，
        /// 所以本项目只读自己目录下的固定清单，下载链接也在同一目录里，互不干扰。
        ///
        /// 检查时这个地址优先于 GitHub；它 404 / 超时才回退到「GitHub 直连 + 加速节点」竞速。
        /// 换仓库、换目录、换自建站点时整条替换即可；留空 = 禁用国内源，只用 GitHub。
        /// 格式见 UpdateService 注释与 README。
        /// </summary>
        [ObservableProperty] private string _updateMirrorManifestUrl =
            "https://gitee.com/yann9/dist/raw/master/TaskScheduler/update.json";

        /// <summary>
        /// 检查更新 / 下载时是否跟随系统代理（默认开）。
        ///
        /// 这一项对国内用户很关键：很多人靠代理软件访问 GitHub，早期版本为了兼容服务模式
        /// 写死 UseProxy=false，等于把所有挂了代理的用户挡在门外。更新逻辑只跑在界面进程，
        /// 可以放心读系统代理；若本机代理配置异常导致连不上，取消勾选即可改为纯直连。
        /// </summary>
        [ObservableProperty] private bool _updateUseSystemProxy = true;

        /// <summary>
        /// GitHub 加速节点前缀。留空 = 使用内置的一组公益节点并自动竞速；
        /// 填 "direct" = 只直连、不走任何加速节点；
        /// 填自定义前缀（多个用英文分号隔开）= 只用这些节点，例如单位自建的反向代理。
        /// 前缀形如 https://gh-proxy.com/ ，程序会把原始 GitHub 链接拼在后面。
        /// </summary>
        [ObservableProperty] private string _updateProxyPrefixes = "";

        /// <summary>用户点过「跳过此版本」的版本号，只忽略这一个版本</summary>
        [ObservableProperty] private string _skippedUpdateVersion = "";

        /// <summary>上次自动检查的日期（yyyy-MM-dd），保证一天最多检查一次</summary>
        [ObservableProperty] private string _lastUpdateCheckDate = "";

        /// <summary>
        /// 把越界值夹回合理区间。设置文件是可以被手工改的（甚至被别的程序写坏），
        /// 一个 MaxRunRecords = 0 会让运行记录永远不保留、-1 会让裁剪逻辑直接抛异常。
        /// </summary>
        public void Normalize()
        {
            if (MaxRunRecords < 10) MaxRunRecords = 10;
            if (MaxRunRecords > 20000) MaxRunRecords = 20000;

            if (LogTailLines < 50) LogTailLines = 50;
            if (LogTailLines > 20000) LogTailLines = 20000;

            if (MaxLogFileMB < 1) MaxLogFileMB = 1;
            if (MaxLogFileMB > 1024) MaxLogFileMB = 1024;

            if (TaskListFontSize < 9) TaskListFontSize = 9;
            if (TaskListFontSize > 20) TaskListFontSize = 20;

            // 这几个字段来自设置文件，可能被手工改成 null；统一收敛成非 null，
            // 免得下游 Trim() / 字符串比较到处判空。
            UpdateManifestUrl = (UpdateManifestUrl ?? "").Trim();
            UpdateMirrorManifestUrl = (UpdateMirrorManifestUrl ?? "").Trim();
            UpdateProxyPrefixes = (UpdateProxyPrefixes ?? "").Trim();
            SkippedUpdateVersion = SkippedUpdateVersion ?? "";
            LastUpdateCheckDate = LastUpdateCheckDate ?? "";
        }

        /// <summary>复制一份，供"取消"时丢弃改动</summary>
        public AppSettings Clone() => new AppSettings
        {
            AutoStartWithWindows = AutoStartWithWindows,
            AutoStartMinimized = AutoStartMinimized,
            AutoStartEngine = AutoStartEngine,
            CloseAction = CloseAction,
            ShowTrayHintOnClose = ShowTrayHintOnClose,
            MaxRunRecords = MaxRunRecords,
            RecordSuccessfulRuns = RecordSuccessfulRuns,
            LogTailLines = LogTailLines,
            MaxLogFileMB = MaxLogFileMB,
            NotifyOnTaskFailure = NotifyOnTaskFailure,
            PreventSleepDuringTasks = PreventSleepDuringTasks,
            ConfirmOnDelete = ConfirmOnDelete,
            TaskListFontSize = TaskListFontSize,
            AutoCheckUpdate = AutoCheckUpdate,
            UpdateManifestUrl = UpdateManifestUrl,
            UpdateMirrorManifestUrl = UpdateMirrorManifestUrl,
            UpdateUseSystemProxy = UpdateUseSystemProxy,
            UpdateProxyPrefixes = UpdateProxyPrefixes,
            SkippedUpdateVersion = SkippedUpdateVersion,
            LastUpdateCheckDate = LastUpdateCheckDate
        };

        /// <summary>把另一份设置的值全部抄过来（保持对象引用不变，界面绑定不会断）</summary>
        public void CopyFrom(AppSettings other)
        {
            if (other == null) return;
            AutoStartWithWindows = other.AutoStartWithWindows;
            AutoStartMinimized = other.AutoStartMinimized;
            AutoStartEngine = other.AutoStartEngine;
            CloseAction = other.CloseAction;
            ShowTrayHintOnClose = other.ShowTrayHintOnClose;
            MaxRunRecords = other.MaxRunRecords;
            RecordSuccessfulRuns = other.RecordSuccessfulRuns;
            LogTailLines = other.LogTailLines;
            MaxLogFileMB = other.MaxLogFileMB;
            NotifyOnTaskFailure = other.NotifyOnTaskFailure;
            PreventSleepDuringTasks = other.PreventSleepDuringTasks;
            ConfirmOnDelete = other.ConfirmOnDelete;
            TaskListFontSize = other.TaskListFontSize;
            AutoCheckUpdate = other.AutoCheckUpdate;
            UpdateManifestUrl = other.UpdateManifestUrl;
            UpdateMirrorManifestUrl = other.UpdateMirrorManifestUrl;
            UpdateUseSystemProxy = other.UpdateUseSystemProxy;
            UpdateProxyPrefixes = other.UpdateProxyPrefixes;
            SkippedUpdateVersion = other.SkippedUpdateVersion;
            LastUpdateCheckDate = other.LastUpdateCheckDate;
        }
    }
}
