using System.Windows;
using TaskScheduler.Engine;
using TaskScheduler.ViewModels;

namespace TaskScheduler.Views
{
    /// <summary>
    /// 「检查更新」窗口。
    /// preloaded 为 null 时由 ViewModel 自己异步检查（手动入口）；
    /// 启动自动检查已经拿到结果时把结果传进来，避免重复发一次网络请求。
    /// </summary>
    public partial class UpdateWindow : Window
    {
        /// <param name="settings">
        /// 可选：用哪份设置检查。设置窗口传它正在编辑的副本，让用户刚填完地址就能立刻验证，
        /// 而不必先点「确定」保存。其余入口留 null，用全局设置。
        /// </param>
        public UpdateWindow(UpdateCheckResult preloaded = null, AppSettings settings = null)
        {
            InitializeComponent();
            DataContext = new UpdateViewModel(preloaded, Close, settings);
        }
    }
}
