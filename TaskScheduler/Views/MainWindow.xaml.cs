using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TaskScheduler.Engine;
using TaskScheduler.ViewModels;

namespace TaskScheduler.Views
{
    public partial class MainWindow : Window
    {
        /// <summary>"已在托盘运行"的气泡只提示一次，之后不再打扰</summary>
        private static bool _trayHintShown;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // 已经是"真退出"流程：放行
            if (App.ForceClose) return;

            var settings = SettingsService.Current;

            if (settings.CloseAction == CloseAction.ExitApp)
            {
                // 用户明确选择"关掉就是退出"。置位后放行，Application 会随之退出，
                // OnExit 里照常 Stop()+Save()。
                App.ForceClose = true;
                return;
            }

            // 最小化到托盘（默认）。这里必须 Cancel，否则窗口一关、本地托管模式下的引擎
            // 就跟着进程一起没了 —— 用户点个 × 结果所有任务都不跑了，那是最坏的行为。
            e.Cancel = true;
            Hide();

            if (settings.ShowTrayHintOnClose && !_trayHintShown)
            {
                _trayHintShown = true;
                // 气泡就一行：长说明在 Windows 托盘气泡里显示不下，退出入口托盘右键本来就有
                App.Tray?.ShowNotification(
                    "程序仍在后台运行",
                    "双击托盘图标可重新打开窗口。");
            }
        }

        private void Window_StateChanged(object sender, System.EventArgs e)
        {
            // 最小化时也收起主窗口（仅留托盘）
            if (WindowState == WindowState.Minimized)
                Hide();
        }

        /// <summary>双击任务列表行 = 打开编辑（与工具栏"编辑"等价，操作更顺手）</summary>
        private void TasksGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            if (vm?.SelectedTask == null) return;
            vm.EditCommand.Execute(null);
        }

        /// <summary>
        /// 彻底绕开系统"菜单右对齐"（SPI_GETMENUDROPALIGNMENT=1）导致的镜像问题。
        /// 用自定义放置回调（PlacementMode.Custom）直接指定 popup 相对菜单项的位置：
        /// 顶层项在其正下方、其余在其右侧——与 MenuDropAlignment 完全无关，左右镜像不再发生。
        /// </summary>
        private void OnMenuItemLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.MenuItem mi) return;
            mi.ApplyTemplate();
            if (mi.Template?.FindName("PART_Popup", mi) is not Popup popup) return;
            if (popup.Tag is bool attached && attached) return; // 已挂过
            popup.Tag = true;
            popup.Placement = PlacementMode.Custom;

            // Aero2 弹层容器模板里硬编码了一条 1px 竖线（图标列右缘，x 固定 ≈32.5），
            // 位置不跟叶子项模板走。叶子项去空白后文字左移，这条线会穿字 —— 藏掉它。
            // 三个时机都做（幂等）：Loaded 时 ScrollViewer 的模板往往还没应用、找不到线；
            // 定位回调早于首帧渲染、可避免闪线；Opened 兜底时线必然已实例化。
            popup.Opened += (s2, e2) => { if (popup.Child != null) HideGutterLines(popup.Child); };
            popup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            {
                if (popup.Child != null) HideGutterLines(popup.Child);
                bool topLevel = mi.Role is System.Windows.Controls.MenuItemRole.TopLevelHeader
                                       or System.Windows.Controls.MenuItemRole.TopLevelItem;
                // 顶层：紧贴菜单头正下方；子项：紧贴该项右侧
                var point = topLevel
                    ? new System.Windows.Point(0, targetSize.Height)
                    : new System.Windows.Point(targetSize.Width, 0);
                return new[] { new CustomPopupPlacement(point, PopupPrimaryAxis.Horizontal) };
            };
            if (popup.Child != null) HideGutterLines(popup.Child);
        }

        /// <summary>右键菜单同款处理：ContextMenu 自身就是弹层内容，线画在它的模板树里。
        /// Opened 时模板必然已应用（Loaded 时机拿不到 ScrollViewer 里的那根线）。</summary>
        private void OnContextMenuLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ContextMenu cm)
                cm.Opened += (s2, e2) => HideGutterLines(cm);
        }

        /// <summary>
        /// 把弹层里"Aero2 图标列分隔线"藏掉：显式 Width=1（或量出来 1~2px 宽且全高）的矩形。
        /// 不会误伤分隔线 —— Separator 模板是 Height=1 的**横**条（宽为全宽，不满足窄高条件）。
        /// </summary>
        private static void HideGutterLines(DependencyObject root)
        {
            // 那根竖线画在弹层 ScrollViewer（SubMenuScrollViewer）自己的模板里，
            // 而 ScrollViewer 的模板延迟到首次布局才应用 —— Loaded 时必须先强制应用，
            // 否则遍历整棵树也找不到它。
            foreach (var sv in EnumerateDescendants(root).OfType<ScrollViewer>())
                sv.ApplyTemplate();

            foreach (var rect in EnumerateDescendants(root)
                         .OfType<System.Windows.Shapes.Rectangle>())
            {
                bool isHairline = (rect.Width > 0 && rect.Width < 3)
                                  || (rect.ActualWidth > 0 && rect.ActualWidth < 3
                                      && rect.ActualHeight > 6);
                if (isHairline)
                    rect.Visibility = Visibility.Collapsed;
            }
        }

        private static IEnumerable<DependencyObject> EnumerateDescendants(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var d in EnumerateDescendants(child)) yield return d;
            }
        }
    }
}
