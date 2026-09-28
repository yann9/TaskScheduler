using System;
using System.IO;

namespace TaskScheduler.Engine
{
    /// <summary>集中管理数据/日志路径，服务与界面共用，避免权限错配</summary>
    public static class Paths
    {
        public static string DataDir
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "TaskScheduler");

        public static string TasksFilePath
            => Path.Combine(DataDir, "tasks.json");

        public static string LogFilePath
            => Path.Combine(DataDir, "scheduler.log");

        /// <summary>界面与服务的共用设置（自启、保留条数、关闭行为…），用户可在设置窗口里改</summary>
        public static string SettingsFilePath
            => Path.Combine(DataDir, "settings.json");

        /// <summary>任务运行记录（环形，只保留最近 N 条，N 由设置决定）</summary>
        public static string RunHistoryFilePath
            => Path.Combine(DataDir, "runs.json");

        /// <summary>
        /// 服务管理操作的"回传通道"。
        /// 服务管理必须提权，于是实际干活的是子进程；而主程序是 WinExe，没有控制台可输出，
        /// 退出码只能表达"成没成"，说不出原因。子进程把 LastMessage 写到这里，父进程读出来展示。
        /// </summary>
        public static string ServiceResultFilePath
            => Path.Combine(DataDir, "service-result.txt");
    }
}
