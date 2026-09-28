using System.Windows;
using WmColor = System.Windows.Media.Color;

namespace TaskScheduler.Views
{
    /// <summary>按钮组合，语义与 System.Windows.MessageBoxButton 一一对应</summary>
    public enum TsDialogButtons
    {
        /// <summary>单个「确定」</summary>
        Ok,
        /// <summary>「是 / 否」，是为主按钮</summary>
        YesNo,
        /// <summary>「确定 / 取消」，确定为主按钮</summary>
        OkCancel
    }

    /// <summary>图标类型（标题行左侧小图标 + 语义色）</summary>
    public enum TsDialogIcon
    {
        /// <summary>提示</summary>
        Info,
        /// <summary>确认提问</summary>
        Question,
        /// <summary>警告</summary>
        Warning,
        /// <summary>错误</summary>
        Error
    }

    /// <summary>
    /// 统一消息对话框 —— 全程序替代系统 MessageBox 的唯一入口
    ///（Win11 内容对话框观感：无边框、圆角 8、阴影、Accent 主按钮）。
    /// 返回值沿用 <see cref="MessageBoxResult"/>，调用点替换零语义差。
    ///
    /// ⚠ 只经由 <see cref="Show"/> 使用（ctor 供探针冒烟用 internal）。
    /// </summary>
    public partial class TsDialog : Window
    {
        private MessageBoxResult _result = MessageBoxResult.OK;

        internal TsDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 显示统一风格的消息框。
        /// owner 为空时自动取当前主窗口 —— 有 owner 才会被正确地压在主窗口前，
        /// 并随主窗口最小化；孤零零悬在屏幕中央的对话框是排障时的噩梦。
        /// </summary>
        public static MessageBoxResult Show(string message, string title = "提示",
            TsDialogButtons buttons = TsDialogButtons.Ok, TsDialogIcon icon = TsDialogIcon.Info,
            Window owner = null)
        {
            var dlg = new TsDialog
            {
                Title = title ?? "提示",
                MessageText = { Text = message ?? "" },
                TitleText = { Text = title ?? "提示" }
            };
            ApplyIcon(dlg, icon);
            ApplyButtons(dlg, buttons);

            var o = owner ?? System.Windows.Application.Current?.MainWindow;
            if (o != null && o != dlg) dlg.Owner = o;

            dlg.ShowDialog();
            return dlg._result;
        }

        private static void ApplyIcon(TsDialog dlg, TsDialogIcon icon)
        {
            // Segoe MDL2 Assets（Win10/11 自带）：E946=Info, E897=Help, E7BA=Warning, E783=Error
            switch (icon)
            {
                case TsDialogIcon.Question:
                    dlg.IconText.Text = "\uE897";
                    dlg.IconText.Foreground = new System.Windows.Media.SolidColorBrush(WmColor.FromRgb(0x00, 0x67, 0xC0));
                    break;
                case TsDialogIcon.Warning:
                    dlg.IconText.Text = "\uE7BA";
                    dlg.IconText.Foreground = new System.Windows.Media.SolidColorBrush(WmColor.FromRgb(0x9A, 0x63, 0x00));
                    break;
                case TsDialogIcon.Error:
                    dlg.IconText.Text = "\uE783";
                    dlg.IconText.Foreground = new System.Windows.Media.SolidColorBrush(WmColor.FromRgb(0xC4, 0x2B, 0x1C));
                    break;
                default:
                    dlg.IconText.Text = "\uE946";
                    dlg.IconText.Foreground = new System.Windows.Media.SolidColorBrush(WmColor.FromRgb(0x00, 0x67, 0xC0));
                    break;
            }
        }

        private static void ApplyButtons(TsDialog dlg, TsDialogButtons buttons)
        {
            // 不在本组的按钮直接从按钮行摘掉；主按钮（Accent）拿焦点
            switch (buttons)
            {
                case TsDialogButtons.YesNo:
                    dlg.BtnOk.Visibility = Visibility.Collapsed;
                    dlg.BtnCancel.Visibility = Visibility.Collapsed;
                    dlg.BtnYes.Focus();
                    break;
                case TsDialogButtons.OkCancel:
                    dlg.BtnYes.Visibility = Visibility.Collapsed;
                    dlg.BtnNo.Visibility = Visibility.Collapsed;
                    dlg.BtnOk.Focus();
                    break;
                default:
                    dlg.BtnYes.Visibility = Visibility.Collapsed;
                    dlg.BtnNo.Visibility = Visibility.Collapsed;
                    dlg.BtnCancel.Visibility = Visibility.Collapsed;
                    dlg.BtnOk.Focus();
                    break;
            }
        }

        private void Btn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe &&
                System.Enum.TryParse(fe.Tag as string, out MessageBoxResult r))
            {
                _result = r;
            }
            Close();
        }
    }
}
