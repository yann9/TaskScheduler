using System;
using System.Globalization;
using System.Windows.Data;

namespace TaskScheduler.Converters
{
    /// <summary>把常用枚举值显示为中文（下拉框友好）</summary>
    public class EnumChineseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "";
            return value switch
            {
                TriggerType t => t switch
                {
                    TriggerType.Time => "时间触发",
                    TriggerType.SystemEvent => "系统事件",
                    TriggerType.FileWatcher => "文件监听",
                    _ => t.ToString()
                },
                TimeTriggerKind k => k switch
                {
                    TimeTriggerKind.OneTime => "一次性",
                    TimeTriggerKind.Daily => "每天",
                    TimeTriggerKind.Weekly => "每周",
                    TimeTriggerKind.Interval => "间隔循环",
                    TimeTriggerKind.Monthly => "每月",
                    _ => k.ToString()
                },
                MonthlyMode mm => mm switch
                {
                    MonthlyMode.DayOfMonth => "每月第几天",
                    MonthlyMode.NthWeekday => "每月第几个星期几",
                    MonthlyMode.LastDay => "每月最后一天",
                    _ => mm.ToString()
                },
                FileStateMode fs => fs switch
                {
                    FileStateMode.Exists => "路径存在",
                    FileStateMode.NotExists => "路径不存在",
                    FileStateMode.ModifiedWithinMinutes => "最近 N 分钟内修改过",
                    FileStateMode.OlderThanMinutes => "已超过 N 分钟未修改",
                    FileStateMode.SizeAtLeastMB => "大小不小于 N MB",
                    _ => fs.ToString()
                },
                SystemEventType s => s switch
                {
                    SystemEventType.Startup => "系统启动",
                    SystemEventType.Logon => "用户登录",
                    SystemEventType.Lock => "系统锁屏",
                    SystemEventType.Unlock => "系统解锁",
                    SystemEventType.Idle => "空闲",
                    SystemEventType.PowerOn => "接入电源",
                    SystemEventType.PowerOff => "切换到电池",
                    SystemEventType.NetworkChanged => "网络地址变化",
                    SystemEventType.SessionEnding => "注销 / 关机前",
                    SystemEventType.DeviceArrived => "设备接入",
                    SystemEventType.ProcessStarted => "进程启动",
                    SystemEventType.ProcessStopped => "进程退出",
                    SystemEventType.RemoteConnect => "远程桌面连接",
                    SystemEventType.RemoteDisconnect => "远程桌面断开",
                    _ => s.ToString()
                },
                FileWatcherChangeType f => f switch
                {
                    FileWatcherChangeType.Created => "新建文件",
                    FileWatcherChangeType.Changed => "文件变更",
                    FileWatcherChangeType.Renamed => "重命名",
                    FileWatcherChangeType.Deleted => "删除",
                    FileWatcherChangeType.All => "全部变化",
                    _ => f.ToString()
                },
                ActionType a => a switch
                {
                    ActionType.RunProgram => "运行程序",
                    ActionType.HttpRequest => "HTTP 请求",
                    ActionType.Notification => "桌面通知",
                    ActionType.FileOperation => "文件操作",
                    ActionType.InputSimulation => "模拟输入",
                    _ => a.ToString()
                },
                HttpMethodKind m => m.ToString(),
                FileOperationKind o => o == FileOperationKind.Copy ? "复制" : "移动",
                InputActionKind i => i switch
                {
                    InputActionKind.KeyboardText => "输入文本",
                    InputActionKind.KeyboardShortcut => "快捷键",
                    InputActionKind.MouseClick => "鼠标点击",
                    _ => i.ToString()
                },
                IntervalUnit u => u == IntervalUnit.Hours ? "小时" : "分钟",
                IntervalFirstRunMode fm => fm switch
                {
                    IntervalFirstRunMode.AfterInterval => "等一个完整间隔",
                    IntervalFirstRunMode.Immediately => "立即执行一次",
                    IntervalFirstRunMode.AfterDelay => "延迟后执行",
                    _ => fm.ToString()
                },
                ConditionType c => c switch
                {
                    ConditionType.NetworkConnected => "网络连通",
                    ConditionType.Power => "电源状态",
                    ConditionType.ProcessRunning => "进程运行中",
                    ConditionType.LocalIp => "本机 IP",
                    ConditionType.DiskSpace => "磁盘空间",
                    ConditionType.FilePath => "文件状态",
                    ConditionType.TimeWindow => "允许时段",
                    ConditionType.HttpHealth => "HTTP 健康检查",
                    ConditionType.SystemLoad => "系统负载",
                    ConditionType.ForegroundWindow => "前台窗口",
                    ConditionType.NetworkType => "网络类型",
                    ConditionType.ServiceState => "服务状态",
                    _ => c.ToString()
                },
                LoadMetric lm => lm == LoadMetric.CpuPercent ? "CPU 占用率" : "可用内存",
                FullScreenMode fsm => fsm switch
                {
                    FullScreenMode.Ignore => "不限制全屏",
                    FullScreenMode.RequireFullScreen => "必须处于全屏",
                    FullScreenMode.RequireWindowed => "必须不是全屏",
                    _ => fsm.ToString()
                },
                NetworkKindType nk => nk switch
                {
                    NetworkKindType.Wired => "有线连接",
                    NetworkKindType.Wireless => "无线连接",
                    NetworkKindType.AnyConnected => "任意已连接网络",
                    _ => nk.ToString()
                },
                PowerRequiredState p => p == PowerRequiredState.PluggedIn ? "接通电源" : "使用电池",
                TaskScheduler.Engine.CloseAction ca => ca switch
                {
                    TaskScheduler.Engine.CloseAction.MinimizeToTray => "最小化到托盘（任务继续运行）",
                    TaskScheduler.Engine.CloseAction.ExitApp => "退出程序（本地托管的任务随之停止）",
                    _ => ca.ToString()
                },
                DayOfWeek d => d switch
                {
                    DayOfWeek.Sunday => "周日",
                    DayOfWeek.Monday => "周一",
                    DayOfWeek.Tuesday => "周二",
                    DayOfWeek.Wednesday => "周三",
                    DayOfWeek.Thursday => "周四",
                    DayOfWeek.Friday => "周五",
                    DayOfWeek.Saturday => "周六",
                    _ => d.ToString()
                },
                _ => value.ToString()
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
