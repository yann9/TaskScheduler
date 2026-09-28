using System;
using System.Threading;
using System.Threading.Tasks;

namespace TaskScheduler.Actions
{
    /// <summary>弹出桌面通知（气泡）</summary>
    public class NotificationAction : ObservableModel, IAction
    {
        public ActionType Type => ActionType.Notification;

        private string _title = "自动化任务";
        public string Title
        {
            get => _title;
            set => Set(ref _title, value);
        }

        private string _message = "";
        public string Message
        {
            get => _message;
            set => Set(ref _message, value);
        }

        public async Task ExecuteAsync(ActionContext context, CancellationToken ct)
        {
            if (context.Dispatcher != null)
                await context.Dispatcher.InvokeAsync(() => context.Notifier.ShowNotification(Title, Message));
            else
                context.Notifier.ShowNotification(Title, Message);
        }

        public string Describe() => $"桌面通知：{Title}";
    }
}
