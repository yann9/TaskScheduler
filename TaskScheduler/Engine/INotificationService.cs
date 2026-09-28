namespace TaskScheduler.Engine
{
    /// <summary>桌面通知服务（由托盘图标实现）</summary>
    public interface INotificationService
    {
        void ShowNotification(string title, string message);
    }

    /// <summary>空实现，供无 UI 上下文时兜底</summary>
    public class NullNotificationService : INotificationService
    {
        public static readonly NullNotificationService Instance = new NullNotificationService();
        public void ShowNotification(string title, string message) { }
    }
}
