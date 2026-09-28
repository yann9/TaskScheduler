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

        // ===== 界面 =====
        [ObservableProperty] private bool _confirmOnDelete = true;
        [ObservableProperty] private int _taskListFontSize = 12;

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
            ConfirmOnDelete = ConfirmOnDelete,
            TaskListFontSize = TaskListFontSize
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
            ConfirmOnDelete = other.ConfirmOnDelete;
            TaskListFontSize = other.TaskListFontSize;
        }
    }
}
