namespace TaskScheduler
{
    /// <summary>触发器大类</summary>
    public enum TriggerType
    {
        Time,
        SystemEvent,
        FileWatcher
    }

    /// <summary>
    /// 时间触发器子类型。
    /// 注意：会随任务持久化（Newtonsoft 默认存数字），新增成员一律追加到末尾。
    /// </summary>
    public enum TimeTriggerKind
    {
        OneTime,
        Daily,
        Weekly,
        Interval,

        /// <summary>每月：固定日 / 第 N 个星期几 / 最后一天</summary>
        Monthly
    }

    /// <summary>每月触发器的“哪一天”方式</summary>
    public enum MonthlyMode
    {
        /// <summary>每月固定第 N 天（遇小月自动落在当月最后一天）</summary>
        DayOfMonth,

        /// <summary>每月第 N 个星期几（例如第 2 个周二）</summary>
        NthWeekday,

        /// <summary>每月最后一天</summary>
        LastDay
    }

    /// <summary>
    /// 系统事件类型。
    /// 注意：与 ConditionType 一样会随任务持久化，新增成员一律追加到末尾。
    /// </summary>
    public enum SystemEventType
    {
        Startup,
        Logon,
        Lock,
        Unlock,
        Idle,

        /// <summary>接入电源（拔插电源适配器 / 电池状态变为在线）</summary>
        PowerOn,

        /// <summary>切换到电池供电（拔掉电源）</summary>
        PowerOff,

        /// <summary>网络地址发生变化（连上 / 断开某个网络，含 DHCP 续约）</summary>
        NetworkChanged,

        /// <summary>即将注销或关机（最后的机会：同步数据、提交日志）</summary>
        SessionEnding,

        /// <summary>有卷接入（U 盘 / 移动硬盘插入）</summary>
        DeviceArrived,

        /// <summary>指定进程启动</summary>
        ProcessStarted,

        /// <summary>指定进程退出</summary>
        ProcessStopped,

        /// <summary>远程桌面连接进来</summary>
        RemoteConnect,

        /// <summary>远程桌面断开</summary>
        RemoteDisconnect
    }

    /// <summary>文件监听关注的变化类型</summary>
    public enum FileWatcherChangeType
    {
        Created,
        Changed,
        Renamed,
        Deleted,
        All
    }

    /// <summary>动作大类</summary>
    public enum ActionType
    {
        RunProgram,
        HttpRequest,
        Notification,
        FileOperation,
        InputSimulation
    }

    /// <summary>HTTP 方法</summary>
    public enum HttpMethodKind
    {
        GET,
        POST,
        PUT,
        DELETE
    }

    /// <summary>文件操作类型</summary>
    public enum FileOperationKind
    {
        Copy,
        Move
    }

    /// <summary>输入模拟类型</summary>
    public enum InputActionKind
    {
        KeyboardText,
        KeyboardShortcut,
        MouseClick
    }

    /// <summary>间隔单位</summary>
    public enum IntervalUnit
    {
        Minutes,
        Hours
    }

    /// <summary>
    /// 间隔触发的"首次执行"策略：一个从未排期过的间隔任务，第一次什么时候跑。
    /// 注意：第一个成员必须是默认值（0），旧存档里没有该字段时反序列化后即为此值，
    /// 从而保持"等一个完整间隔"的老行为，不会让已有任务在升级后突然多跑一次。
    /// </summary>
    public enum IntervalFirstRunMode
    {
        /// <summary>等一个完整间隔（默认，等同旧行为）</summary>
        AfterInterval,

        /// <summary>启动后立即执行首次</summary>
        Immediately,

        /// <summary>延迟指定时长后执行首次（用于等系统/网络就绪）</summary>
        AfterDelay
    }

    /// <summary>
    /// 执行条件大类（触发后、动作前需全部满足）。
    /// 新增成员一律追加到末尾：ConditionType 会随任务一起持久化，插在中间会让老存档的条件类型错位。
    /// </summary>
    public enum ConditionType
    {
        NetworkConnected,
        Power,
        ProcessRunning,

        /// <summary>本机 IPv4 地址匹配（用于"只有接在公司网段时才执行"）</summary>
        LocalIp,

        /// <summary>磁盘剩余空间（低于阈值才清理之类）</summary>
        DiskSpace,

        /// <summary>文件 / 目录状态（存在、大小、最近修改时间）</summary>
        FilePath,

        /// <summary>允许执行的时段（例如只在 08:00–20:00 跑）</summary>
        TimeWindow,

        /// <summary>HTTP 健康检查（目标返回预期状态码 / 响应体含指定文本）</summary>
        HttpHealth,

        /// <summary>系统负载（CPU 占用、可用内存）</summary>
        SystemLoad,

        /// <summary>前台窗口（标题关键词、是否全屏）</summary>
        ForegroundWindow,

        /// <summary>网络类型（有线 / 无线 / 任意已连接）</summary>
        NetworkType,

        /// <summary>Windows 服务状态</summary>
        ServiceState
    }

    /// <summary>系统负载条件关注的指标</summary>
    public enum LoadMetric
    {
        /// <summary>全机 CPU 占用率（百分比）</summary>
        CpuPercent,

        /// <summary>可用物理内存（MB）</summary>
        FreeMemoryMB
    }

    /// <summary>前台窗口条件对"全屏"的要求</summary>
    public enum FullScreenMode
    {
        /// <summary>不限制</summary>
        Ignore,

        /// <summary>要求前台窗口处于全屏</summary>
        RequireFullScreen,

        /// <summary>要求前台窗口不是全屏</summary>
        RequireWindowed
    }

    /// <summary>网络类型条件的判定口径</summary>
    public enum NetworkKindType
    {
        /// <summary>有线连接（排除无线、VPN 隧道与拨号）</summary>
        Wired,

        /// <summary>无线连接（WiFi）</summary>
        Wireless,

        /// <summary>任意已连接的网络（有网就行）</summary>
        AnyConnected
    }

    /// <summary>文件 / 目录状态条件的判定方式</summary>
    public enum FileStateMode
    {
        /// <summary>路径存在（文件或目录）</summary>
        Exists,

        /// <summary>路径不存在（常用于"等文件被搬走再跑"）</summary>
        NotExists,

        /// <summary>最后修改时间在 N 分钟以内（文件是新鲜的）</summary>
        ModifiedWithinMinutes,

        /// <summary>最后修改时间超过 N 分钟（文件已经稳定下来，适合搬运 / 上传）</summary>
        OlderThanMinutes,

        /// <summary>文件大小不小于 N MB</summary>
        SizeAtLeastMB
    }

    /// <summary>电源条件要求的供电状态</summary>
    public enum PowerRequiredState
    {
        PluggedIn,
        OnBattery
    }
}
