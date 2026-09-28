using System.Windows;

namespace TaskScheduler.Views
{
    public partial class LogWindow : Window
    {
        public LogWindow() => InitializeComponent();

        /// <summary>是否跟随最新日志自动滚到底（用户往上翻历史时自动暂停跟随）</summary>
        private bool _follow = true;

        private void LogBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!_follow) return;
            // 显式写全名：本项目同时引用了 WinForms，TextBox 这类名字有歧义
            ((System.Windows.Controls.TextBox)sender).ScrollToEnd();
        }

        private void LogBox_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
        {
            // 整段 Text 重新赋值会把滚动位置打回顶部；用户在底部时我们跟随（ScrollToEnd），
            // 一旦他手动往上翻，就不再把他拽回底部。
            var box = (System.Windows.Controls.TextBox)sender;
            if (box.ExtentHeight <= 0 || box.ViewportHeight <= 0) return;
            _follow = box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 4;
        }
    }
}
