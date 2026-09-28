namespace TaskScheduler.Engine
{
    /// <summary>
    /// 服务内（无桌面会话）的通知实现：仅写日志。
    /// session 0 无法直接弹气泡，需要桌面提示的动作请在“本地模式”或由 UI 进程处理。
    /// </summary>
    public class ServiceNotificationService : INotificationService
    {
        public void ShowNotification(string title, string message)
            => Log.Write($"[通知] {title} - {message}");
    }
}
